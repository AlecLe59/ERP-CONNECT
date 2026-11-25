using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Security.Audit;
using ErpConnect.Storage.Repositories;

namespace ErpConnect.Engine;

/// <summary>Journal d'audit chaîné, en ajout seul.</summary>
public sealed class ChainedAuditLog : IAuditLog, IDisposable
{
    private readonly AuditRepository _repository;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ChainedAuditLog(AuditRepository repository, IClock clock, IEngineEventSink? events = null)
    {
        _repository = repository;
        _clock = clock;
        Events = events ?? NullEngineEventSink.Instance;
    }

    public IEngineEventSink Events { get; set; }

    public async Task<AuditEvent> AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        AuditEvent appended;
        try
        {
            // Horodatage tronqué à la précision stockée (100 ns) pour que l'empreinte soit recalculable.
            var now = _clock.UtcNow.ToUniversalTime();
            var timestamp = new DateTimeOffset(now.Ticks, TimeSpan.Zero);
            appended = await _repository.AppendAsync(
                entry, timestamp, (previous, sequence) => AuditHasher.Compute(previous, sequence, timestamp, entry), AuditHasher.GenesisHash, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Events.AuditAppended(appended.Sequence, appended.Action, appended.Outcome);
        return appended;
    }

    public Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct) => _repository.QueryAsync(query, ct);

    public async Task<AuditVerification> VerifyChainAsync(CancellationToken ct) =>
        AuditHasher.Verify(await _repository.ReadAllOrderedAsync(ct).ConfigureAwait(false));

    public void Dispose() => _gate.Dispose();
}
