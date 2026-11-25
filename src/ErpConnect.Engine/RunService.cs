using System.Collections.Concurrent;
using System.Text.Json;
using ErpConnect.Connectors.Sdk;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Licensing;
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
    /// <summary>Droits de licence. Null : tout autorisé (tests du noyau).</summary>
    public IEntitlements? Entitlements { get; init; }

    private readonly FlowService _flows;
    private readonly ConnectionService _connections;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;
    private readonly IEngineEventSink _events;
    private readonly RunServiceOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, ActiveRun> _active = new(StringComparer.Ordinal);
    private readonly IDedupRegistry? _dedup;
    private readonly IAlertRepository? _alerts;

    public RunService(IRunRepository runs, FlowService flows, ConnectionService connections, IAuditLog audit, IClock clock, IEngineEventSink? events = null, RunServiceOptions? options = null,
        IDedupRegistry? dedup = null, IAlertRepository? alerts = null)
    {
        _dedup = dedup ?? runs as IDedupRegistry;
        _alerts = alerts ?? runs as IAlertRepository;
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
        await LicenseService.RequireAsync(Entitlements, Features.Run, ct).ConfigureAwait(false);
        if (trigger is "schedule" or "retry")
        {
            await LicenseService.RequireAsync(Entitlements, Features.Scheduler, ct).ConfigureAwait(false);
        }

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

    /// <summary>
    /// Reprise : une exécution Suspendue repart sur place depuis son point de reprise (compteurs conservés) ;
    /// une exécution en Échec pour erreur transitoire crée une exécution fille qui repart du point de reprise de la mère.
    /// </summary>
    public async Task<RunRecord> ResumeAsync(CallerIdentity caller, string runId, string correlationId, CancellationToken ct, string trigger = "resume")
    {
        ArgumentNullException.ThrowIfNull(caller);
        var run = await _runs.GetAsync(runId, ct).ConfigureAwait(false) ?? throw new EngineException(EngineErrorKind.NotFound, "Exécution introuvable.");
        if (run.Status == RunStatus.Failed)
        {
            if (run.ErrorKind != "transient")
            {
                throw new EngineException(EngineErrorKind.Conflict, "Seule une exécution en échec pour erreur transitoire (réseau, indisponibilité) peut être reprise ; corrigez la cause puis relancez.");
            }

            return await StartAsync(caller, run.FlowId, false, correlationId, ct, trigger, run).ConfigureAwait(false);
        }

        if (run.Status != RunStatus.Suspended)
        {
            throw new EngineException(EngineErrorKind.Conflict, "Seule une exécution suspendue ou en échec transitoire peut être reprise.");
        }

        var (_, definition) = await _flows.LoadAsync(run.FlowId, run.FlowVersion, ct).ConfigureAwait(false);
        if (!await _runs.TransitionAsync(run.Id, RunStatus.Suspended, RunStatus.Pending, caller.UserName, "Reprise", _clock.UtcNow, ct).ConfigureAwait(false))
        {
            throw new EngineException(EngineErrorKind.Conflict, "L'exécution a changé d'état entre-temps.");
        }

        await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "runs.resume", $"run/{run.Id}", AuditOutcomes.Success,
            JsonSerializer.Serialize(new { mode = "inPlace", checkpoint = run.Checkpoint is not null }), correlationId), ct).ConfigureAwait(false);
        var pending = run with { Status = RunStatus.Pending };
        _events.RunChanged(run.Id, run.FlowId, RunStatus.Pending);
        var active = new ActiveRun(Task.CompletedTask);
        _active[run.Id] = active;
        active.Task = Task.Run(() => ExecuteGuardedAsync(pending, definition, active), CancellationToken.None);
        return pending;
    }

    /// <summary>Indique si une exécution de ce flux est en cours dans cette instance (le planificateur évite les chevauchements).</summary>
    public async Task<bool> HasActiveRunAsync(string flowId, CancellationToken ct)
    {
        foreach (var id in _active.Keys)
        {
            var r = await _runs.GetAsync(id, ct).ConfigureAwait(false);
            if (r?.FlowId == flowId)
            {
                return true;
            }
        }

        return false;
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
            await FailAsync(run, $"Erreur interne : {ex.GetType().Name}", CancellationToken.None, "internal").ConfigureAwait(false);
        }
        finally
        {
            _active.TryRemove(run.Id, out _);
            active.Stop.Dispose();
        }
    }

    private async Task FailAsync(RunRecord run, string error, CancellationToken ct, string kind = "permanent")
    {
        var current = await _runs.GetAsync(run.Id, ct).ConfigureAwait(false);
        if (current is null || current.Status.IsTerminal())
        {
            return;
        }

        await _runs.SetErrorAsync(run.Id, error, ct, kind).ConfigureAwait(false);
        await _runs.LogAsync(run.Id, _clock.UtcNow, "error", error, ct).ConfigureAwait(false);
        if (current.Status == RunStatus.Pending)
        {
            await _runs.TransitionAsync(run.Id, RunStatus.Pending, RunStatus.Running, "engine", null, _clock.UtcNow, ct).ConfigureAwait(false);
        }

        await _runs.TransitionAsync(run.Id, RunStatus.Running, RunStatus.Failed, "engine", Truncate(error, 500), _clock.UtcNow, ct).ConfigureAwait(false);
        _events.RunChanged(run.Id, run.FlowId, RunStatus.Failed);
        await RaiseAlertAsync(run, "error", $"Exécution {run.Id} en échec : {Truncate(error, 400)}", ct).ConfigureAwait(false);
    }

    private async Task RaiseAlertAsync(RunRecord run, string severity, string message, CancellationToken ct)
    {
        if (_alerts is null)
        {
            return;
        }

        var alert = await _alerts.RaiseAsync(run.FlowId, run.Id, severity, message, _clock.UtcNow, ct).ConfigureAwait(false);
        _events.AlertRaised(alert.Id, alert.FlowId, alert.Severity, alert.Message);
    }

    /// <summary>Clé métier → empreinte SHA-256 (hex, 32 premiers caractères). Une clé vide ou nulle est refusée.</summary>
    internal static string DedupHash(object? value)
    {
        var text = ExpressionCompiler.ToText(value);
        if (text.Length == 0)
        {
            throw new ExpressionException("clé vide.");
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..32];
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
            if (FlowService.UsesScripts(def) && !await _flows.ScriptsAllowedAsync(ct).ConfigureAwait(false))
            {
                throw new EngineException(EngineErrorKind.Forbidden, "Ce flux contient un script et les scripts sont désactivés dans les paramètres ou non inclus dans la licence.");
            }

            pipeline = RecordPipeline.Compile(def);
        }
        catch (Exception ex) when (ex is EngineException or FlowValidationException or ConnectorException)
        {
            await FailAsync(run, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        using var pipelineLifetime = pipeline;
        var transactional = targetConnector.Manifest.Capabilities.HasFlag(ConnectorCapabilities.FileTransport);
        var cursor = transactional ? null : run.Checkpoint;
        if (run.Checkpoint is not null)
        {
            await _runs.LogAsync(run.Id, _clock.UtcNow, "info", transactional
                ? "Reprise : cible fichier (tout ou rien), traitement depuis le début."
                : $"Reprise depuis le point {run.Checkpoint}.", ct).ConfigureAwait(false);
        }

        long read = 0, written = 0, rejected = 0, skipped = 0, batchNo = 0;
        if (cursor is not null && run.ParentRunId is null)
        {
            (read, written, rejected, skipped) = (run.ReadCount, run.WrittenCount, run.RejectedCount, run.SkippedCount); // reprise sur place
        }

        CompiledExpression? dedupKey = null;
        if (!string.IsNullOrWhiteSpace(def.DedupKey))
        {
            dedupKey = ExpressionCompiler.Compile(def.DedupKey);
            if (_dedup is null)
            {
                await FailAsync(run, "Registre anti-doublons indisponible.", ct, "internal").ConfigureAwait(false);
                return;
            }
        }

        var committed = (Read: read, Written: written, Rejected: rejected, Skipped: skipped, Cursor: cursor);
        var seenThisRun = new HashSet<string>(StringComparer.Ordinal);
        var pendingKeys = new List<string>(); // cible fichier : enregistrées seulement après publication
        var cancelled = false;
        string? fatal = null;
        string? ackWarning = null;
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
                    var toWrite = new List<(int Index, Record Record, Record Source, string? Key)>(batch.Records.Count);
                    string?[] keys = new string?[batch.Records.Count];
                    IReadOnlySet<string> known = new HashSet<string>();
                    if (dedupKey is not null)
                    {
                        for (var i = 0; i < batch.Records.Count; i++)
                        {
                            try
                            {
                                keys[i] = DedupHash(dedupKey.Evaluate(batch.Records[i]));
                            }
                            catch (ExpressionException ex)
                            {
                                rejects.Add(Reject(run.Id, batchNo, i, "mapping", $"Clé anti-doublon : {ex.Message}", batch.Records[i]));
                            }
                        }

                        known = await _dedup!.ExistingAsync(run.FlowId, keys.OfType<string>().ToList(), ct).ConfigureAwait(false);
                    }

                    for (var i = 0; i < batch.Records.Count; i++)
                    {
                        if (dedupKey is not null && keys[i] is null)
                        {
                            continue; // déjà rejeté (clé non calculable)
                        }

                        if (keys[i] is { } k && (known.Contains(k) || !seenThisRun.Add(k)))
                        {
                            skipped++; // doublon : déjà transmis par une exécution précédente ou plus haut dans celle-ci
                            continue;
                        }

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
                            toWrite.Add((i, m.Output, batch.Records[i], keys[i]));
                        }
                    }

                    if (toWrite.Count > 0)
                    {
                        var outcomes = await WriteWithRetryAsync(writer, toWrite.Select(t => t.Record).ToList(), ct).ConfigureAwait(false);
                        if (outcomes.FirstOrDefault(o => !o.Success && o.Kind == ErrorKind.Transient) is { } stillTransient)
                        {
                            // Indisponibilité persistante de la cible : ce n'est pas un rejet métier. Le lot n'est pas confirmé
                            // et sera renvoyé en entier à la reprise (au moins une fois).
                            throw new ConnectorException(ErrorKind.Transient, stillTransient.Error ?? "Cible indisponible.");
                        }

                        for (var k = 0; k < outcomes.Count; k++)
                        {
                            if (outcomes[k].Success)
                            {
                                written++;
                                if (toWrite[k].Key is { } key)
                                {
                                    pendingKeys.Add(key);
                                }
                            }
                            else
                            {
                                rejects.Add(Reject(run.Id, batchNo, toWrite[k].Index, "write", outcomes[k].Error ?? "Refusé par la cible.", toWrite[k].Source));
                            }
                        }
                    }

                    read += batch.Records.Count;
                    rejected += rejects.Count;
                    IReadOnlyList<string>? committedKeys = null;
                    if (!transactional && pendingKeys.Count > 0)
                    {
                        committedKeys = pendingKeys.ToArray();
                        pendingKeys.Clear();
                    }

                    await _runs.SaveProgressAsync(run.Id, new RunProgress(read, written, rejected, skipped, batch.Cursor, rejects, committedKeys), ct).ConfigureAwait(false);
                    committed = (read, written, rejected, skipped, batch.Cursor);
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
                    if (pendingKeys.Count > 0)
                    {
                        await _dedup!.RegisterAsync(run.FlowId, run.Id, pendingKeys, _clock.UtcNow, ct).ConfigureAwait(false);
                    }

                    if (reader is ISourceAcknowledgement ack && read > 0)
                    {
                        try
                        {
                            await ack.AcknowledgeAsync(ct).ConfigureAwait(false);
                        }
                        catch (ConnectorException ex)
                        {
                            // Les données sont écrites : l'exécution réussit, mais la source sera relue la prochaine fois.
                            ackWarning = $"Données écrites, mais la source n'a pas pu être marquée comme traitée : {ex.Message}";
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ConnectorException or IOException or UnauthorizedAccessException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            // Seule la progression confirmée est conservée : le point de reprise reste celui du dernier lot enregistré.
            await _runs.SaveProgressAsync(run.Id, new RunProgress(committed.Read, committed.Written, committed.Rejected, committed.Skipped, transactional ? null : committed.Cursor, []), CancellationToken.None).ConfigureAwait(false);
            var transient = ConnectorException.Classify(ex) == ErrorKind.Transient;
            await FailAsync(run, $"Erreur {(transient ? "transitoire" : "permanente")} : {ex.Message}", ct, transient ? "transient" : "permanent").ConfigureAwait(false);
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
            await FailAsync(run, reason, ct, "rejects").ConfigureAwait(false);
            return;
        }

        if (read == 0)
        {
            await _runs.LogAsync(run.Id, _clock.UtcNow, "warning", "Aucun enregistrement lu.", ct).ConfigureAwait(false);
            await _runs.TransitionAsync(run.Id, RunStatus.Running, RunStatus.SucceededWithWarnings, "engine", "Aucun enregistrement", _clock.UtcNow, ct).ConfigureAwait(false);
            _events.RunChanged(run.Id, run.FlowId, RunStatus.SucceededWithWarnings);
            await RaiseAlertAsync(run, "warning", $"Exécution {run.Id} : aucun enregistrement lu.", ct).ConfigureAwait(false);
            return;
        }

        if (ackWarning is not null)
        {
            await _runs.LogAsync(run.Id, _clock.UtcNow, "warning", $"{ackWarning} {summary}", ct).ConfigureAwait(false);
            await _runs.TransitionAsync(run.Id, RunStatus.Running, RunStatus.SucceededWithWarnings, "engine", "Source non marquée", _clock.UtcNow, ct).ConfigureAwait(false);
            _events.RunChanged(run.Id, run.FlowId, RunStatus.SucceededWithWarnings);
            await RaiseAlertAsync(run, "warning", $"Exécution {run.Id} : {ackWarning}", ct).ConfigureAwait(false);
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

            await _options.Retry.WaitBeforeAsync(attempt, null, ct).ConfigureAwait(false);
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
