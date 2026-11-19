using System.Text.Json;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Flows;

namespace ErpConnect.Engine;

/// <summary>Applique les durées de rétention des paramètres (toutes les 6 heures et au démarrage). Chaque purge non vide est auditée.</summary>
public sealed class RetentionService : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly IRetentionStore _store;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;

    public RetentionService(SettingsService settings, IRetentionStore store, IAuditLog audit, IClock clock)
    {
        _settings = settings;
        _store = store;
        _audit = audit;
        _clock = clock;
    }

    public async Task<PurgeCounts> PurgeAsync(CancellationToken ct)
    {
        var s = (await _settings.GetAsync(ct).ConfigureAwait(false)).Settings;
        var now = _clock.UtcNow;
        var cutoffs = new RetentionCutoffs(
            now.AddDays(-s.RunHeaderRetentionDays), now.AddDays(-s.SucceededLogRetentionDays), now.AddDays(-s.FailedLogRetentionDays),
            now.AddDays(-s.RejectRetentionDays), now.AddDays(-s.DeduplicationRetentionDays));
        var counts = await _store.PurgeAsync(cutoffs, ct).ConfigureAwait(false);
        if (counts.Total > 0)
        {
            await _audit.AppendAsync(new AuditEntry("S-1-5-18", "ERP Connect Engine", "retention.purge", "runs", AuditOutcomes.Success,
                JsonSerializer.Serialize(counts), "retention"), ct).ConfigureAwait(false);
        }

        return counts;
    }

    public void Start(TimeSpan? period = null)
    {
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _loopTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period ?? TimeSpan.FromHours(6));
            do
            {
                try
                {
                    await PurgeAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // Une purge ratée (base occupée) est retentée au passage suivant.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop is not null)
        {
            await _loop.CancelAsync().ConfigureAwait(false);
            try
            {
                if (_loopTask is not null)
                {
                    await _loopTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            _loop.Dispose();
        }
    }
}
