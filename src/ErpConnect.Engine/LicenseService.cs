using System.Text.Json;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Licensing;
using ErpConnect.Security.Licensing;

namespace ErpConnect.Engine;

/// <summary>state : evaluation | evaluationExpired | licensed | expired | invalid (licence enregistrée mais refusée : autre installation, signature).</summary>
public sealed record LicenseStatus(
    string State,
    string InstallationId,
    DateTimeOffset? EvaluationEndsAt,
    LicensePayload? License,
    string? Problem,
    DateTimeOffset? ImportedAt,
    string? ImportedBy,
    bool CanVerify,
    int PublishedFlows);

/// <summary>
/// Licence hors ligne : l'éditeur signe une licence liée à l'identifiant d'installation ; l'administrateur l'importe.
/// Sans licence, 30 jours d'évaluation complète depuis l'installation ; ensuite, plus d'exécution ni de publication
/// (la lecture, l'audit et l'export restent possibles : les données ne sont jamais prises en otage).
/// </summary>
public sealed class LicenseService : IEntitlements
{
    public static readonly TimeSpan EvaluationPeriod = TimeSpan.FromDays(30);

    private readonly ILicenseStore _store;
    private readonly LicenseVerifier _verifier;
    private readonly IFlowRepository _flows;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;
    private readonly string _installationId;
    private readonly DateTimeOffset _installedAt;

    public LicenseService(ILicenseStore store, LicenseVerifier verifier, IFlowRepository flows, IAuditLog audit, IClock clock, string installationId, DateTimeOffset installedAt)
    {
        _store = store;
        _verifier = verifier;
        _flows = flows;
        _audit = audit;
        _clock = clock;
        _installationId = installationId;
        _installedAt = installedAt;
    }

    public async Task<LicenseStatus> GetStatusAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var published = (await _flows.ListAsync(ct).ConfigureAwait(false)).Count(f => f.PublishedVersion is not null);
        var stored = await _store.GetAsync(ct).ConfigureAwait(false);
        var evaluationEnd = _installedAt + EvaluationPeriod;
        LicenseStatus Evaluation(string? problem) => new(now < evaluationEnd ? "evaluation" : "evaluationExpired", _installationId, evaluationEnd, null, problem,
            stored?.ImportedAt, stored?.ImportedBy, _verifier.HasTrustedKey, published);
        if (stored is null)
        {
            return Evaluation(null);
        }

        LicensePayload license;
        try
        {
            license = _verifier.Verify(stored.Content);
        }
        catch (LicenseException ex)
        {
            return Evaluation(ex.Message) with { State = "invalid" };
        }

        if (!string.Equals(license.InstallationId, _installationId, StringComparison.OrdinalIgnoreCase))
        {
            return Evaluation("Licence émise pour une autre installation.") with { State = "invalid", License = license };
        }

        var state = license.ExpiresAt is { } end && now >= end ? "expired" : "licensed";
        return new LicenseStatus(state, _installationId, null, license, null, stored.ImportedAt, stored.ImportedBy, true, published);
    }

    public async Task<EntitlementDecision> CheckAsync(string feature, CancellationToken ct)
    {
        var s = await GetStatusAsync(ct).ConfigureAwait(false);
        switch (s.State)
        {
            case "evaluation":
                return EntitlementDecision.Yes;
            case "licensed":
                break;
            case "expired":
                return EntitlementDecision.No($"Licence expirée le {s.License!.ExpiresAt:yyyy-MM-dd}. Importez une licence renouvelée (écran Licence).");
            case "invalid":
                return EntitlementDecision.No($"Licence refusée : {s.Problem}");
            default:
                return EntitlementDecision.No("Période d'évaluation terminée. Importez une licence (écran Licence).");
        }

        var license = s.License!;
        return feature switch
        {
            Features.Run => EntitlementDecision.Yes,
            Features.PublishFlow when license.MaxPublishedFlows is { } max && s.PublishedFlows >= max =>
                EntitlementDecision.No($"La licence autorise {max} flux publiés au maximum."),
            Features.PublishFlow => EntitlementDecision.Yes,
            _ when license.Features.Contains(feature, StringComparer.Ordinal) => EntitlementDecision.Yes,
            _ => EntitlementDecision.No($"Fonction « {feature} » non incluse dans la licence ({license.Edition})."),
        };
    }

    public async Task<LicenseStatus> ImportAsync(CallerIdentity caller, string content, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        LicensePayload license;
        try
        {
            license = _verifier.Verify(content);
        }
        catch (LicenseException ex)
        {
            await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "license.import", "license", AuditOutcomes.Failure,
                JsonSerializer.Serialize(new { reason = ex.Message }), correlationId), ct).ConfigureAwait(false);
            throw new EngineException(EngineErrorKind.Validation, ex.Message, [new Core.Settings.FieldError("content", ex.Message)]);
        }

        if (!string.Equals(license.InstallationId, _installationId, StringComparison.OrdinalIgnoreCase))
        {
            const string message = "Cette licence a été émise pour une autre installation.";
            await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "license.import", "license", AuditOutcomes.Failure,
                JsonSerializer.Serialize(new { reason = message, license.LicenseId }), correlationId), ct).ConfigureAwait(false);
            throw new EngineException(EngineErrorKind.Validation, message, [new Core.Settings.FieldError("content", message)]);
        }

        await _store.SaveAsync(new StoredLicense(content, license.LicenseId, _clock.UtcNow, caller.UserName), ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "license.import", $"license/{license.LicenseId}", AuditOutcomes.Success,
            JsonSerializer.Serialize(new { license.LicenseId, license.Customer, license.Edition, license.ExpiresAt, license.Features, license.MaxPublishedFlows }), correlationId), ct).ConfigureAwait(false);
        return await GetStatusAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(CallerIdentity caller, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var removed = await _store.DeleteAsync(ct).ConfigureAwait(false);
        if (removed)
        {
            await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "license.remove", "license", AuditOutcomes.Success, "{}", correlationId), ct).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>Lève une erreur de licence si la fonction n'est pas couverte.</summary>
    public static async Task RequireAsync(IEntitlements? entitlements, string feature, CancellationToken ct)
    {
        if (entitlements is null)
        {
            return;
        }

        var d = await entitlements.CheckAsync(feature, ct).ConfigureAwait(false);
        if (!d.Allowed)
        {
            throw new EngineException(EngineErrorKind.Entitlement, d.Reason!);
        }
    }
}
