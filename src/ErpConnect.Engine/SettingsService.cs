using System.Text.Json;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Settings;

namespace ErpConnect.Engine;

public sealed class SettingsService
{
    private readonly ISettingsRepository _repository;
    private readonly IAuditLog _audit;
    private readonly IEngineEventSink _events;

    public SettingsService(ISettingsRepository repository, IAuditLog audit, IEngineEventSink? events = null)
    {
        _repository = repository;
        _audit = audit;
        _events = events ?? NullEngineEventSink.Instance;
    }

    public Task<VersionedSettings> GetAsync(CancellationToken ct) => _repository.GetAsync(ct);

    public async Task<VersionedSettings> UpdateAsync(CallerIdentity caller, EngineSettings settings, long expectedVersion, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new EngineException(EngineErrorKind.Validation, "Paramètres invalides.", errors);
        }

        var before = await _repository.GetAsync(ct).ConfigureAwait(false);
        var updated = await _repository.TryUpdateAsync(settings, expectedVersion, caller.UserName, ct).ConfigureAwait(false)
            ?? throw new EngineException(EngineErrorKind.Conflict, "Les paramètres ont été modifiés entre-temps. Rechargez puis recommencez.");

        await _audit.AppendAsync(new AuditEntry(
            caller.UserSid, caller.UserName, "settings.update", "settings/engine", AuditOutcomes.Success,
            JsonSerializer.Serialize(new { fromVersion = before.Version, toVersion = updated.Version, changes = Diff(before.Settings, settings) }),
            correlationId), ct).ConfigureAwait(false);
        _events.SettingsChanged(updated.Version, updated.UpdatedBy);
        return updated;
    }

    private static Dictionary<string, object?[]> Diff(EngineSettings a, EngineSettings b)
    {
        var diff = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        foreach (var p in typeof(EngineSettings).GetProperties())
        {
            var va = p.GetValue(a);
            var vb = p.GetValue(b);
            if (!Equals(va, vb))
            {
                diff[p.Name] = [va, vb];
            }
        }

        return diff;
    }
}
