using System.Collections.Concurrent;
using System.Text.Json;
using ErpConnect.Connectors.Sdk;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Runs;
using ErpConnect.Flows;

namespace ErpConnect.Engine;

public sealed record RunServiceOptions
{
    public int MaxConcurrentRuns { get; init; } = 4;

    /// <summary>Nouvelles tentatives d'un lot ou d'un enregistrement en erreur transitoire.</summary>
    public RetryPolicy Retry { get; init; } = new(5);

    public int MaxRejectJsonLength { get; init; } = 8192;
}

/// <summary>
/// Noyau d'exécution par lots. Pour chaque lot : lecture, mapping, écriture, puis enregistrement atomique
/// des compteurs, des rejets et du point de reprise. Règle métier : un seul rejet = exécution en Échec ;
/// une cible fichier n'est alors pas publiée (tout ou rien). L'annulation prend effet après le lot en cours.
/// </summary>
public sealed class RunService : IAsyncDisposable
{
    private readonly IRunRepository _runs;
    private readonly FlowService _flows;
    private readonly ConnectionService _connections;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;
    private readonly IEngineEventSink _events;
    private readonly RunServiceOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, ActiveRun> _active = new(StringComparer.Ordinal);

    public RunService(IRunRepository runs, FlowService flows, ConnectionService connections, IAuditLog audit, IClock clock, IEngineEventSink? events = null, RunServiceOptions? options = null)
    {
        _runs = runs;
        _flows = flows;
        _connections = connections;
        _audit = audit;
        _clock = clock;
        _events = events ?? NullEngineEventSink.Instance;
        _options = options ?? new RunServiceOptions();
        _slots = new SemaphoreSlim(_options.MaxConcurrentRuns, _options.MaxConcurrentRuns);
    }

    private sealed class ActiveRun(Task task)
    {
        public CancellationTokenSource Stop { get; } = new();

        public Task Task { get; set; } = task;
    }

    public int ActiveCount => _active.Count;

    public Task<RunRecord?> GetAsync(string id, CancellationToken ct) => _runs.GetAsync(id, ct);

    public Task<(IReadOnlyList<RunRecord> Items, int Total)> QueryAsync(RunQuery query, CancellationToken ct) => _runs.QueryAsync(query, ct);

    public Task<IReadOnlyList<RunRejectRecord>> RejectsAsync(string id, int limit, CancellationToken ct) => _runs.RejectsAsync(id, limit, ct);

    public Task<IReadOnlyList<RunLogRecord>> LogsAsync(string id, int limit, CancellationToken ct) => _runs.LogsAsync(id, limit, ct);

    /// <summary>Au démarrage : une exécution restée En cours ou En attente (arrêt brutal) passe en Suspendu, pour reprise.</summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var run in await _runs.InStatusAsync([RunStatus.Running, RunStatus.Pending], ct).ConfigureAwait(false))
        {
            if (await _runs.TransitionAsync(run.Id, run.Status, RunStatus.Suspended, "engine", "Interruption du service", _clock.UtcNow, ct).ConfigureAwait(false))
            {
                await _runs.LogAsync(run.Id, _clock.UtcNow, "warning", "Exécution interrompue par un arrêt du service : suspendue, reprise possible.", ct).ConfigureAwait(false);
                count++;
            }
        }

        return count;
    }

    /// <summary>Démarre une exécution. useDraft = tester la dernière version enregistrée plutôt que la version publiée.</summary>
    public async Task<RunRecord> StartAsync(CallerIdentity caller, string flowId, bool useDraft, string correlationId, CancellationToken ct, string trigger = "manual", RunRecord? parent = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var flow = await _flows.GetAsync(flowId, ct).ConfigureAwait(false) ?? throw new EngineException(EngineErrorKind.NotFound, "Flux introuvable.");
        var version = parent?.FlowVersion ?? (useDraft ? flow.DraftVersion : flow.PublishedVersion ?? throw new EngineException(EngineErrorKind.Conflict, "Flux non publié : publiez-le ou lancez un test de la version en cours."));
        var (_, definition) = await _flows.LoadAsync(flowId, version, ct).ConfigureAwait(false);
        var run = new RunRecord(NewRunId(), flowId, version, RunStatus.Pending, caller.UserName, parent?.Id, _clock.UtcNow, null, null, 0, 0, 0, 0, parent?.Checkpoint, null, trigger);
        await _runs.CreateAsync(run, ct).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, parent is null ? "runs.start" : "runs.resume", $"run/{run.Id}", AuditOutcomes.Success,
            JsonSerializer.Serialize(new { flowId, version, trigger, parent = parent?.Id }), correlationId), ct).ConfigureAwait(false);
        _events.RunChanged(run.Id, flowId, RunStatus.Pending);
        var active = new ActiveRun(Task.CompletedTask);
        _active[run.Id] = active;
        active.Task = Task.Run(() => ExecuteGuardedAsync(run, definition, active), CancellationToken.None);
        return run;
    }

    private string NewRunId() => $"{_clock.UtcNow.UtcDateTime:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";

    public async Task<bool> CancelAsync(CallerIdentity caller, string runId, bool administrator, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var run = await _runs.GetAsync(runId, ct).ConfigureAwait(false) ?? throw new EngineException(EngineErrorKind.NotFound, "Exécution introuvable.");
        var actor = administrator ? RunActor.Administrator : RunActor.Operator;
        var check = RunStateMachine.Check(run.Status, RunStatus.Cancelled, actor);
        if (!check.Allowed)
        {
            throw new EngineException(EngineErrorKind.Conflict, check.RefusalReason!);
        }

        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "runs.cancel", $"run/{runId}", AuditOutcomes.Success, null, correlationId), ct).ConfigureAwait(false);
        if (_active.TryGetValue(runId, out var active))
        {
            await _runs.LogAsync(runId, _clock.UtcNow, "warning", $"Annulation demandée par {caller.UserName} : arrêt après le lot en cours.", ct).ConfigureAwait(false);
            try
            {
                await active.Stop.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // terminée entre-temps
            }

            return true;
        }

        var done = await _runs.TransitionAsync(runId, run.Status, RunStatus.Cancelled, caller.UserName, "Annulation", _clock.UtcNow, ct).ConfigureAwait(false);
        if (done)
        {
            _events.RunChanged(runId, run.FlowId, RunStatus.Cancelled);
        }

        return done;
    }

    /// <summary>Attend la fin d'une exécution lancée par cette instance (tests, arrêt propre).</summary>
    public Task WaitAsync(string runId) => _active.TryGetValue(runId, out var a) ? a.Task : Task.CompletedTask;

    private async Task ExecuteGuardedAsync(RunRecord run, FlowDefinition definition, ActiveRun active)
    {
        try
        {
            await _slots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(run, definition, active.Stop.Token).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            var current = await _runs.GetAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
            if (current is { Status: RunStatus.Running or RunStatus.Pending })
            {
                await _runs.TransitionAsync(run.Id, current.Status, RunStatus.Suspended, "engine", "Arrêt du service", _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Dernier rempart : une exécution ne doit jamais faire tomber le moteur.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await FailAsync(run, $"Erreur interne : {ex.GetType().Name}", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _active.TryRemove(run.Id, out _);
            active.Stop.Dispose();
        }
    }

    private async Task FailAsync(RunRecord run, string error, CancellationToken ct)
    {
        var current = await _runs.GetAsync(run.Id, ct).ConfigureAwait(false);
        if (current is null || current.Status.IsTerminal())
        {
            return;
        }

        await _runs.SetErrorAsync(run.Id, error, ct).ConfigureAwait(false);
        await _runs.LogAsync(run.Id, _clock.UtcNow, "error", error, ct).ConfigureAwait(false);
        if (current.Status == RunStatus.Pending)
        {
            await _runs.TransitionAsync(run.Id, RunStatus.Pending, RunStatus.Running, "engine", null, _clock.UtcNow, ct).ConfigureAwait(false);
        }

        await _runs.TransitionAsync(run.Id, RunStatus.Running, RunStatus.Failed, "engine", Truncate(error, 500), _clock.UtcNow, ct).ConfigureAwait(false);
        _events.RunChanged(run.Id, run.FlowId, RunStatus.Failed);
    }

    private static string Truncate(string s, int n) => s.Length > n ? s[..n] : s;

    private async Task ExecuteAsync(RunRecord run, FlowDefinition def, CancellationToken stop)
    {
        var ct = _shutdown.Token;
        if (!await _runs.TransitionAsync(run.Id, RunStatus.Pending, RunStatus.Running, "engine", null, _clock.UtcNow, ct).ConfigureAwait(false))
        {
            return; // annulée avant démarrage
        }

        _events.RunChanged(run.Id, run.FlowId, RunStatus.Running);
        await _runs.LogAsync(run.Id, _clock.UtcNow, "info", $"Démarrage (version {run.FlowVersion}).", ct).ConfigureAwait(false);

        IConnector sourceConnector, targetConnector;
        ConnectorSettings sourceSettings, targetSettings;
        RecordPipeline pipeline;
        try
        {
            (sourceConnector, sourceSettings) = await _connections.ResolveAsync(def.Source.ConnectionId, ct).ConfigureAwait(false);
            (targetConnector, targetSettings) = await _connections.ResolveAsync(def.Target.ConnectionId, ct).ConfigureAwait(false);
            pipeline = RecordPipeline.Compile(def);
        }
        catch (Exception ex) when (ex is EngineException or FlowValidationException or ConnectorException)
        {
            await FailAsync(run, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        var transactional = targetConnector.Manifest.Capabilities.HasFlag(ConnectorCapabilities.FileTransport);
        var cursor = transactional ? null : run.Checkpoint;
        if (run.Checkpoint is not null)
        {
            await _runs.LogAsync(run.Id, _clock.UtcNow, "info", transactional
                ? "Reprise : cible fichier (tout ou rien), traitement depuis le début."
                : $"Reprise depuis le point {run.Checkpoint}.", ct).ConfigureAwait(false);
        }

        long read = 0, written = 0, rejected = 0, skipped = 0, batchNo = 0;
        var cancelled = false;
        string? fatal = null;
        var reader = sourceConnector.CreateReader(sourceSettings, new ReadRequest(def.Source.Resource, def.BatchSize, FlowDefinition.ToDictionary(def.Source.Options)));
        var writer = targetConnector.CreateWriter(targetSettings, new WriteRequest(def.Target.Resource,
            def.Target.Mode == "overwrite" ? WriteMode.Overwrite : WriteMode.CreateNew, FlowDefinition.ToDictionary(def.Target.Options), run.Id));
        try
        {
            await using (reader.ConfigureAwait(false))
            await using (writer.ConfigureAwait(false))
            {
                await foreach (var batch in reader.ReadAsync(cursor, ct).ConfigureAwait(false))
                {
                    batchNo++;
                    var rejects = new List<RunRejectRecord>();
                    var toWrite = new List<(int Index, Record Record, Record Source)>(batch.Records.Count);
                    for (var i = 0; i < batch.Records.Count; i++)
                    {
                        var m = pipeline.Process(batch.Records[i]);
                        if (m.Fate == RecordFate.Filtered)
                        {
                            skipped++;
                        }
                        else if (m.Output is null)
                        {
                            rejects.Add(Reject(run.Id, batchNo, i, "mapping", m.RejectReason!, batch.Records[i]));
                        }
                        else
                        {
                            toWrite.Add((i, m.Output, batch.Records[i]));
                        }
                    }

                    if (toWrite.Count > 0)
                    {
                        var outcomes = await WriteWithRetryAsync(writer, toWrite.Select(t => t.Record).ToList(), ct).ConfigureAwait(false);
                        for (var k = 0; k < outcomes.Count; k++)
                        {
                            if (outcomes[k].Success)
                            {
                                written++;
                            }
                            else
                            {
                                rejects.Add(Reject(run.Id, batchNo, toWrite[k].Index, "write", outcomes[k].Error ?? "Refusé par la cible.", toWrite[k].Source));
                            }
                        }
                    }

                    read += batch.Records.Count;
                    rejected += rejects.Count;
                    await _runs.SaveProgressAsync(run.Id, new RunProgress(read, written, rejected, skipped, batch.Cursor, rejects), ct).ConfigureAwait(false);
                    if (rejected >= def.MaxRejects)
                    {
                        fatal = $"Arrêt : {rejected} rejet(s), seuil de {def.MaxRejects} atteint.";
                        break;
                    }

                    if (stop.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                }

                if (!cancelled && fatal is null && rejected == 0)
                {
                    await writer.CompleteAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is ConnectorException or IOException or UnauthorizedAccessException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            await _runs.SaveProgressAsync(run.Id, new RunProgress(read, written, rejected, skipped, null, []), CancellationToken.None).ConfigureAwait(false);
            var kind = ConnectorException.Classify(ex) == ErrorKind.Transient ? "transitoire" : "permanente";
            await FailAsync(run, $"Erreur {kind} : {ex.Message}", ct).ConfigureAwait(false);
            return;
        }

        var summary = $"{read} lu(s), {written} écrit(s), {skipped} filtré(s), {rejected} rejeté(s).";
        if (cancelled)
        {
            await _runs.LogAsync(run.Id, _clock.UtcNow, "warning", $"Annulée après le lot {batchNo}. {summary}", ct).ConfigureAwait(false);
            await _runs.TransitionAsync(run.Id, RunStatus.Running, RunStatus.Cancelled, "engine", "Annulation", _clock.UtcNow, ct).ConfigureAwait(false);
            _events.RunChanged(run.Id, run.FlowId, RunStatus.Cancelled);
            return;
        }

        if (fatal is not null || rejected > 0)
        {
            var reason = fatal ?? $"{rejected} rejet(s) : l'exécution est en échec{(transactional ? ", aucun fichier n'a été publié" : string.Empty)}. {summary}";
            await FailAsync(run, reason, ct).ConfigureAwait(false);
            return;
        }

        if (read == 0)
        {
            await _runs.LogAsync(run.Id, _clock.UtcNow, "warning", "Aucun enregistrement lu.", ct).ConfigureAwait(false);
            await _runs.TransitionAsync(run.Id, RunStatus.Running, RunStatus.SucceededWithWarnings, "engine", "Aucun enregistrement", _clock.UtcNow, ct).ConfigureAwait(false);
            _events.RunChanged(run.Id, run.FlowId, RunStatus.SucceededWithWarnings);
            return;
        }

        await _runs.LogAsync(run.Id, _clock.UtcNow, "info", $"Terminée. {summary}", ct).ConfigureAwait(false);
        await _runs.TransitionAsync(run.Id, RunStatus.Running, RunStatus.Succeeded, "engine", null, _clock.UtcNow, ct).ConfigureAwait(false);
        _events.RunChanged(run.Id, run.FlowId, RunStatus.Succeeded);
    }

    /// <summary>Écrit un lot ; les enregistrements en échec transitoire sont réessayés seuls, avec attente exponentielle.</summary>
    private async Task<IReadOnlyList<RecordOutcome>> WriteWithRetryAsync(IRecordWriter writer, List<Record> records, CancellationToken ct)
    {
        var result = await _options.Retry.ExecuteAsync((_, token) => writer.WriteBatchAsync(records, token), ct).ConfigureAwait(false);
        var outcomes = result.Outcomes.ToArray();
        for (var attempt = 1; attempt < _options.Retry.MaxAttempts; attempt++)
        {
            var transient = outcomes.Where(o => !o.Success && o.Kind == ErrorKind.Transient).Select(o => o.Index).ToList();
            if (transient.Count == 0)
            {
                break;
            }

            await Task.Delay(_options.Retry.DelayFor(attempt, null), ct).ConfigureAwait(false);
            var retry = await writer.WriteBatchAsync(transient.Select(i => records[i]).ToList(), ct).ConfigureAwait(false);
            for (var k = 0; k < transient.Count; k++)
            {
                outcomes[transient[k]] = retry.Outcomes[k] with { Index = transient[k] };
            }
        }

        return outcomes;
    }

    private RunRejectRecord Reject(string runId, long batch, int index, string stage, string reason, Record source) =>
        new(0, runId, batch, index, stage, Truncate(reason, 2000), RecordJson.Serialize(source, _options.MaxRejectJsonLength), _clock.UtcNow);

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_active.Values.Select(a => a.Task)).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _shutdown.Dispose();
        _slots.Dispose();
    }
}
