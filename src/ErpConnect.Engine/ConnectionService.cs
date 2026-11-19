using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ErpConnect.Connectors.Sdk;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Settings;

namespace ErpConnect.Engine;

/// <summary>Connexions : configuration non secrète en base, secrets dans le coffre (connection/{id}/{champ}).</summary>
public sealed partial class ConnectionService
{
    public const string SecretUsage = "connection";
    private readonly IConnectionRepository _repository;
    private readonly IFlowRepository _flows;
    private readonly ISecretVault _vault;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;

    public ConnectionService(IConnectionRepository repository, IFlowRepository flows, ISecretVault vault, IAuditLog audit, IClock clock, IReadOnlyList<IConnector> connectors)
    {
        _repository = repository;
        _flows = flows;
        _vault = vault;
        _audit = audit;
        _clock = clock;
        Connectors = connectors;
    }

    public IReadOnlyList<IConnector> Connectors { get; }

    public IConnector? FindConnector(string id) => Connectors.FirstOrDefault(c => string.Equals(c.Manifest.Id, id, StringComparison.Ordinal));

    public Task<IReadOnlyList<ConnectionRecord>> ListAsync(CancellationToken ct) => _repository.ListAsync(ct);

    public Task<ConnectionRecord?> GetAsync(string id, CancellationToken ct) => _repository.GetAsync(id, ct);

    private static string SecretId(string connectionId, string field) => $"connection/{connectionId}/{field}";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// Crée ou modifie une connexion. Un secret absent ou vide conserve la valeur existante ;
    /// <paramref name="clearSecrets"/> efface explicitement des secrets.
    /// </summary>
    public async Task<ConnectionRecord> SaveAsync(
        CallerIdentity caller, string? id, string name, string connectorId, IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?> secrets, IReadOnlyList<string> clearSecrets, long expectedVersion, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(clearSecrets);
        var connector = FindConnector(connectorId) ?? throw Validation("connectorId", "Connecteur inconnu.");
        var schema = connector.Manifest.ConfigSchema;
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            errors.Add(new FieldError("name", "Entre 1 et 100 caractères."));
        }

        ConnectionRecord? existing = null;
        if (expectedVersion > 0)
        {
            existing = await _repository.GetAsync(id ?? string.Empty, ct).ConfigureAwait(false) ?? throw new EngineException(EngineErrorKind.NotFound, "Connexion introuvable.");
            if (existing.ConnectorId != connectorId)
            {
                errors.Add(new FieldError("connectorId", "Le type de connecteur ne peut pas changer."));
            }
        }

        id = existing?.Id ?? (string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N")[..12] : id);
        if (!IdPattern().IsMatch(id))
        {
            errors.Add(new FieldError("id", "Identifiant : minuscules, chiffres et tirets."));
        }

        foreach (var key in secrets.Keys.Concat(clearSecrets))
        {
            if (!schema.Any(f => f.Secret && f.Name == key))
            {
                errors.Add(new FieldError($"secrets.{key}", "Champ secret inconnu."));
            }
        }

        var plain = values.Where(kv => schema.Any(f => !f.Secret && f.Name == kv.Key) || !schema.Any(f => f.Name == kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var key in values.Keys.Where(k => schema.Any(f => f.Secret && f.Name == k)))
        {
            errors.Add(new FieldError(key, "Champ secret : à transmettre dans « secrets »."));
        }

        var secretFields = new HashSet<string>(existing?.SecretFields ?? [], StringComparer.Ordinal);
        secretFields.ExceptWith(clearSecrets);
        foreach (var (k, v) in secrets)
        {
            if (!string.IsNullOrEmpty(v))
            {
                secretFields.Add(k);
            }
        }

        // Validation contre le schéma : un secret présent est représenté par un marqueur (jamais sa valeur).
        var validation = new ConnectorSettings(plain, secretFields.ToDictionary(s => s, _ => "•", StringComparer.Ordinal)).ValidateAgainst(schema);
        errors.AddRange(validation.Select(e => new FieldError(e.Field, e.Message)));
        if (errors.Count > 0)
        {
            throw new EngineException(EngineErrorKind.Validation, "Connexion invalide.", errors);
        }

        var record = new ConnectionRecord(id, name.Trim(), connectorId, plain, secretFields.Order(StringComparer.Ordinal).ToArray(), expectedVersion, _clock.UtcNow, caller.UserName);
        var saved = await _repository.SaveAsync(record, expectedVersion, ct).ConfigureAwait(false)
            ?? throw new EngineException(EngineErrorKind.Conflict, "Connexion modifiée entre-temps, ou nom déjà utilisé.");

        foreach (var (k, v) in secrets.Where(s => !string.IsNullOrEmpty(s.Value)))
        {
            await _vault.StoreAsync(SecretId(id, k), SecretUsage, Encoding.UTF8.GetBytes(v!), ct).ConfigureAwait(false);
        }

        foreach (var k in clearSecrets)
        {
            await _vault.DeleteAsync(SecretId(id, k), ct).ConfigureAwait(false);
        }

        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "connections.save", $"connection/{id}", AuditOutcomes.Success,
            JsonSerializer.Serialize(new
            {
                version = saved.Version,
                connector = connectorId,
                fields = plain.Keys.Order(StringComparer.Ordinal),
                secretsChanged = secrets.Where(s => !string.IsNullOrEmpty(s.Value)).Select(s => s.Key).Order(StringComparer.Ordinal),
                secretsCleared = clearSecrets,
            }), correlationId), ct).ConfigureAwait(false);
        return saved;
    }

    public async Task<bool> DeleteAsync(CallerIdentity caller, string id, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var existing = await _repository.GetAsync(id, ct).ConfigureAwait(false) ?? throw new EngineException(EngineErrorKind.NotFound, "Connexion introuvable.");
        var users = await _flows.FlowsUsingConnectionAsync(id, ct).ConfigureAwait(false);
        if (users.Count > 0)
        {
            throw new EngineException(EngineErrorKind.Conflict, $"Connexion utilisée par : {string.Join(", ", users)}.");
        }

        var deleted = await _repository.DeleteAsync(id, ct).ConfigureAwait(false);
        foreach (var f in existing.SecretFields)
        {
            await _vault.DeleteAsync(SecretId(id, f), ct).ConfigureAwait(false);
        }

        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "connections.delete", $"connection/{id}", AuditOutcomes.Success, null, correlationId), ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>Configuration déchiffrée pour l'exécution. Ne sort jamais du moteur.</summary>
    public async Task<(IConnector Connector, ConnectorSettings Settings)> ResolveAsync(string id, CancellationToken ct)
    {
        var record = await _repository.GetAsync(id, ct).ConfigureAwait(false) ?? throw new EngineException(EngineErrorKind.NotFound, $"Connexion introuvable : {id}.");
        var connector = FindConnector(record.ConnectorId) ?? throw new EngineException(EngineErrorKind.NotFound, $"Connecteur non installé : {record.ConnectorId}.");
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in record.SecretFields)
        {
            var bytes = await _vault.ResolveAsync(SecretId(id, f), SecretUsage, ct).ConfigureAwait(false);
            if (bytes is not null)
            {
                secrets[f] = Encoding.UTF8.GetString(bytes);
                Array.Clear(bytes);
            }
        }

        return (connector, new ConnectorSettings(record.Settings, secrets));
    }

    public async Task<ConnectionTestResult> TestAsync(CallerIdentity caller, string id, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var (connector, settings) = await ResolveAsync(id, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        ConnectionTestResult result;
        try
        {
            result = await connector.TestAsync(settings, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new ConnectionTestResult(false, "Délai de 60 s dépassé.", TimeSpan.FromSeconds(60));
        }

        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "connections.test", $"connection/{id}",
            result.Success ? AuditOutcomes.Success : AuditOutcomes.Failure, JsonSerializer.Serialize(new { message = result.Message }), correlationId), ct).ConfigureAwait(false);
        return result;
    }

    private static EngineException Validation(string field, string message) => new(EngineErrorKind.Validation, message, [new FieldError(field, message)]);
}
