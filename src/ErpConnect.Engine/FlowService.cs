using System.Text.Json;
using System.Text.RegularExpressions;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Settings;
using ErpConnect.Flows;

namespace ErpConnect.Engine;

/// <summary>Flux versionnés : chaque enregistrement crée une version immuable ; l'exécution planifiée utilise la version publiée.</summary>
public sealed partial class FlowService
{
    private readonly IFlowRepository _repository;
    private readonly IConnectionRepository _connections;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;

    /// <summary>Politique « scripts autorisés » (paramètre ScriptsEnabled). Null : autorisés (tests du noyau).</summary>
    public Func<CancellationToken, Task<bool>>? ScriptsAllowed { get; init; }

    public async Task<bool> ScriptsAllowedAsync(CancellationToken ct) => ScriptsAllowed is null || await ScriptsAllowed(ct).ConfigureAwait(false);

    public static bool UsesScripts(FlowDefinition definition) => definition?.AllSteps.Any(s => s.Kind == "script") ?? false;

    public FlowService(IFlowRepository repository, IConnectionRepository connections, IAuditLog audit, IClock clock)
    {
        _repository = repository;
        _connections = connections;
        _audit = audit;
        _clock = clock;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex IdPattern();

    public Task<IReadOnlyList<FlowRecord>> ListAsync(CancellationToken ct) => _repository.ListAsync(ct);

    public Task<FlowRecord?> GetAsync(string id, CancellationToken ct) => _repository.GetAsync(id, ct);

    public Task<FlowVersionRecord?> GetVersionAsync(string id, long version, CancellationToken ct) => _repository.GetVersionAsync(id, version, ct);

    public async Task<(FlowRecord Flow, FlowDefinition Definition)> LoadAsync(string id, long? version, CancellationToken ct)
    {
        var flow = await _repository.GetAsync(id, ct).ConfigureAwait(false) ?? throw new EngineException(EngineErrorKind.NotFound, "Flux introuvable.");
        var v = version ?? flow.DraftVersion;
        var record = await _repository.GetVersionAsync(id, v, ct).ConfigureAwait(false) ?? throw new EngineException(EngineErrorKind.NotFound, $"Version {v} introuvable.");
        return (flow, FlowDefinition.Parse(record.DefinitionJson));
    }

    public async Task<FlowRecord> SaveAsync(CallerIdentity caller, string? id, string name, string description, FlowDefinition definition, long expectedDraftVersion, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(definition);
        var errors = definition.Validate().Select(e => new FieldError(e.Field, e.Message)).ToList();
        if (UsesScripts(definition) && !await ScriptsAllowedAsync(ct).ConfigureAwait(false))
        {
            errors.Add(new FieldError("steps", "Les scripts sont désactivés (Paramètres > Scripts, réservé à l'administrateur)."));
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            errors.Add(new FieldError("name", "Entre 1 et 100 caractères."));
        }

        if ((description ?? string.Empty).Length > 2000)
        {
            errors.Add(new FieldError("description", "2000 caractères maximum."));
        }

        id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N")[..12] : id;
        if (!IdPattern().IsMatch(id))
        {
            errors.Add(new FieldError("id", "Identifiant : minuscules, chiffres et tirets."));
        }

        foreach (var (field, connectionId) in new[] { ("source.connectionId", definition.Source.ConnectionId), ("target.connectionId", definition.Target.ConnectionId) })
        {
            if (!string.IsNullOrEmpty(connectionId) && await _connections.GetAsync(connectionId, ct).ConfigureAwait(false) is null)
            {
                errors.Add(new FieldError(field, "Connexion inconnue."));
            }
        }

        if (errors.Count > 0)
        {
            throw new EngineException(EngineErrorKind.Validation, "Flux invalide.", errors);
        }

        var saved = await _repository.SaveAsync(id, name.Trim(), description ?? string.Empty, definition.ToJson(), expectedDraftVersion, caller.UserName, _clock.UtcNow, ct).ConfigureAwait(false)
            ?? throw new EngineException(EngineErrorKind.Conflict, "Flux modifié entre-temps, ou nom déjà utilisé.");
        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "flows.save", $"flow/{id}", AuditOutcomes.Success,
            JsonSerializer.Serialize(new { version = saved.DraftVersion }), correlationId), ct).ConfigureAwait(false);
        return saved;
    }

    public async Task<FlowRecord> PublishAsync(CallerIdentity caller, string id, long version, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var published = await _repository.PublishAsync(id, version, caller.UserName, _clock.UtcNow, ct).ConfigureAwait(false)
            ?? throw new EngineException(EngineErrorKind.NotFound, "Flux ou version introuvable.");
        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "flows.publish", $"flow/{id}", AuditOutcomes.Success,
            JsonSerializer.Serialize(new { version }), correlationId), ct).ConfigureAwait(false);
        return published;
    }
}
