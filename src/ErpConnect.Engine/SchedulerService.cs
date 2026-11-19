using System.Text.Json;
using Cronos;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Runs;
using ErpConnect.Core.Settings;

namespace ErpConnect.Engine;

/// <summary>
/// Planificateur : une planification par flux (cron à 5 champs + fuseau horaire). Règles :
/// pas de chevauchement (une occurrence est sautée si le flux tourne encore), une seule exécution de rattrapage
/// après un arrêt du service quel que soit le nombre d'occurrences manquées, nouvelles tentatives à délai exponentiel
/// uniquement pour une erreur transitoire, en reprenant au point de reprise. Seule la version publiée est exécutée.
/// </summary>
public sealed class SchedulerService : IAsyncDisposable
{
    public static readonly CallerIdentity System = new("S-1-5-18", "Planificateur", []);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(24);
    private readonly IScheduleRepository _schedules;
    private readonly FlowService _flows;
    private readonly RunService _runs;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;
    private readonly IAlertRepository? _alerts;
    private readonly IEngineEventSink _events;
    private readonly SemaphoreSlim _tick = new(1, 1);
    private CancellationTokenSource? _loop;
    private Task? _loopTask;

    public SchedulerService(IScheduleRepository schedules, FlowService flows, RunService runs, IAuditLog audit, IClock clock, IAlertRepository? alerts = null, IEngineEventSink? events = null)
    {
        _schedules = schedules;
        _flows = flows;
        _runs = runs;
        _audit = audit;
        _clock = clock;
        _alerts = alerts;
        _events = events ?? NullEngineEventSink.Instance;
    }

    public Task<IReadOnlyList<ScheduleRecord>> ListAsync(CancellationToken ct) => _schedules.ListAsync(ct);

    public static DateTimeOffset? Next(string cron, string timeZone, DateTimeOffset after) =>
        Parse(cron).GetNextOccurrence(after, FindZone(timeZone));

    private static CronExpression Parse(string cron)
    {
        try
        {
            return CronExpression.Parse(cron, CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            throw new EngineException(EngineErrorKind.Validation, "Expression cron invalide.", [new FieldError("cron", $"Format attendu : minute heure jour mois jour-semaine ({ex.Message})")]);
        }
    }

    private static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new EngineException(EngineErrorKind.Validation, "Fuseau horaire inconnu.", [new FieldError("timeZone", "Exemple : Europe/Paris ou Romance Standard Time.")]);
        }
    }

    public async Task<ScheduleRecord> SaveAsync(CallerIdentity caller, string flowId, string cron, string timeZone, bool enabled, int maxRetries, int retryDelaySeconds, long expectedVersion, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var errors = new List<FieldError>();
        if (maxRetries is < 0 or > 10)
        {
            errors.Add(new("maxRetries", "Entre 0 et 10."));
        }

        if (retryDelaySeconds is < 10 or > 86400)
        {
            errors.Add(new("retryDelaySeconds", "Entre 10 s et 24 h."));
        }

        if (errors.Count > 0)
        {
            throw new EngineException(EngineErrorKind.Validation, "Planification invalide.", errors);
        }

        var flow = await _flows.GetAsync(flowId, ct).ConfigureAwait(false) ?? throw new EngineException(EngineErrorKind.NotFound, "Flux introuvable.");
        if (enabled && flow.PublishedVersion is null)
        {
            throw new EngineException(EngineErrorKind.Validation, "Publiez le flux avant d'activer sa planification.", [new FieldError("enabled", "Flux non publié.")]);
        }

        var next = enabled ? Next(cron, timeZone, _clock.UtcNow) : null;
        if (!enabled)
        {
            _ = Parse(cron);
            _ = FindZone(timeZone);
        }

        var saved = await _schedules.SaveAsync(new ScheduleRecord(flowId, cron.Trim(), timeZone, enabled, maxRetries, retryDelaySeconds, 0, _clock.UtcNow, caller.UserName, next, null, 0, null), expectedVersion, ct).ConfigureAwait(false)
            ?? throw new EngineException(EngineErrorKind.Conflict, expectedVersion == 0 ? "Ce flux a déjà une planification." : "La planification a été modifiée entre-temps : rechargez.");
        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "schedules.save", $"flow/{flowId}", AuditOutcomes.Success,
            JsonSerializer.Serialize(new { cron = saved.Cron, timeZone, enabled, maxRetries, retryDelaySeconds, version = saved.Version }), correlationId), ct).ConfigureAwait(false);
        return saved;
    }

    public async Task<bool> DeleteAsync(CallerIdentity caller, string flowId, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var deleted = await _schedules.DeleteAsync(flowId, ct).ConfigureAwait(false);
        if (!deleted)
        {
            throw new EngineException(EngineErrorKind.NotFound, "Planification introuvable.");
        }

        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "schedules.delete", $"flow/{flowId}", AuditOutcomes.Success, null, correlationId), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Démarre la boucle (toutes les 15 s). Les tests appellent <see cref="TickAsync"/> directement.</summary>
    public void Start(TimeSpan? period = null)
    {
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _loopTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period ?? TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // Le planificateur ne doit jamais s'arrêter sur une erreur ponctuelle (base occupée, etc.).
                catch (Exception)
#pragma warning restore CA1031
                {
                    // nouvel essai au prochain tick
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>Un passage du planificateur. Renvoie les exécutions lancées.</summary>
    public async Task<IReadOnlyList<RunRecord>> TickAsync(CancellationToken ct)
    {
        await _tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var started = new List<RunRecord>();
            var now = _clock.UtcNow;
            foreach (var s in await _schedules.ListAsync(ct).ConfigureAwait(false))
            {
                if (s.Enabled)
                {
                    var run = await TickOneAsync(s, now, ct).ConfigureAwait(false);
                    if (run is not null)
                    {
                        started.Add(run);
                    }
                }
            }

            return started;
        }
        finally
        {
            _tick.Release();
        }
    }

    private async Task<RunRecord?> TickOneAsync(ScheduleRecord s, DateTimeOffset now, CancellationToken ct)
    {
        var retryAttempt = s.RetryAttempt;
        var retryAt = s.RetryAt;
        var last = s.LastRunId is null ? null : await _runs.GetAsync(s.LastRunId, ct).ConfigureAwait(false);
        if (last is not null && last.Status.IsTerminal() && retryAt is null)
        {
            if (last.Status == RunStatus.Failed && last.ErrorKind == "transient" && retryAttempt < s.MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(s.RetryDelaySeconds * Math.Pow(2, retryAttempt));
                retryAt = now + (delay > MaxRetryDelay ? MaxRetryDelay : delay); // horloge du planificateur : constatation de l'échec
                retryAttempt++;
                await _schedules.SetStateAsync(s.FlowId, s.NextRunAt, s.LastRunId, retryAttempt, retryAt, ct).ConfigureAwait(false);
            }
            else if (last.Status == RunStatus.Failed && last.ErrorKind == "transient" && s.MaxRetries > 0 && retryAttempt >= s.MaxRetries && retryAttempt != int.MaxValue)
            {
                await AlertAsync(s.FlowId, last.Id, $"Flux {s.FlowId} : échec après {s.MaxRetries} nouvelle(s) tentative(s).", ct).ConfigureAwait(false);
                retryAttempt = int.MaxValue; // alerte émise une seule fois
                await _schedules.SetStateAsync(s.FlowId, s.NextRunAt, s.LastRunId, retryAttempt, null, ct).ConfigureAwait(false);
            }
        }

        if (retryAt is { } due && due <= now && last is not null)
        {
            if (await _runs.HasActiveRunAsync(s.FlowId, ct).ConfigureAwait(false))
            {
                return null;
            }

            var retry = await _runs.ResumeAsync(System, last.Id, "scheduler", ct, "retry").ConfigureAwait(false);
            await _schedules.SetStateAsync(s.FlowId, s.NextRunAt, retry.Id, retryAttempt, null, ct).ConfigureAwait(false);
            return retry;
        }

        if (s.NextRunAt is not { } next || next > now)
        {
            return null;
        }

        var following = Next(s.Cron, s.TimeZone, now); // les occurrences manquées sont regroupées en une seule
        if (await _runs.HasActiveRunAsync(s.FlowId, ct).ConfigureAwait(false))
        {
            await _schedules.SetStateAsync(s.FlowId, following, s.LastRunId, retryAttempt, retryAt, ct).ConfigureAwait(false);
            await AlertAsync(s.FlowId, s.LastRunId, $"Flux {s.FlowId} : occurrence de {next:yyyy-MM-dd HH:mm} sautée, l'exécution précédente n'est pas terminée.", ct, "warning").ConfigureAwait(false);
            return null;
        }

        try
        {
            var run = await _runs.StartAsync(System, s.FlowId, false, "scheduler", ct, "schedule").ConfigureAwait(false);
            await _schedules.SetStateAsync(s.FlowId, following, run.Id, 0, null, ct).ConfigureAwait(false);
            return run;
        }
        catch (EngineException ex)
        {
            await _schedules.SetStateAsync(s.FlowId, following, s.LastRunId, retryAttempt, retryAt, ct).ConfigureAwait(false);
            await AlertAsync(s.FlowId, null, $"Flux {s.FlowId} : lancement planifié impossible ({ex.Message})", ct).ConfigureAwait(false);
            return null;
        }
    }

    private async Task AlertAsync(string flowId, string? runId, string message, CancellationToken ct, string severity = "error")
    {
        if (_alerts is null)
        {
            return;
        }

        var a = await _alerts.RaiseAsync(flowId, runId, severity, message, _clock.UtcNow, ct).ConfigureAwait(false);
        _events.AlertRaised(a.Id, a.FlowId, a.Severity, a.Message);
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop is not null)
        {
            await _loop.CancelAsync().ConfigureAwait(false);
            if (_loopTask is not null)
            {
                try
                {
                    await _loopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _loop.Dispose();
        }

        _tick.Dispose();
    }
}
