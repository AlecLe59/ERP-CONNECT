using ErpConnect.Core.Runs;

namespace ErpConnect.Core.Flows;

/// <summary>Connexion enregistrée. Les valeurs secrètes ne sont jamais ici : seulement la liste des champs secrets renseignés.</summary>
public sealed record ConnectionRecord(
    string Id,
    string Name,
    string ConnectorId,
    IReadOnlyDictionary<string, string?> Settings,
    IReadOnlyList<string> SecretFields,
    long Version,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record FlowRecord(
    string Id,
    string Name,
    string Description,
    long DraftVersion,
    long? PublishedVersion,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record FlowVersionRecord(
    string FlowId,
    long Version,
    string DefinitionJson,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? PublishedAt,
    string? PublishedBy);

public sealed record RunRecord(
    string Id,
    string FlowId,
    long FlowVersion,
    RunStatus Status,
    string TriggeredBy,
    string? ParentRunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long ReadCount,
    long WrittenCount,
    long RejectedCount,
    long SkippedCount,
    string? Checkpoint,
    string? Error,
    string? Trigger,
    string? ErrorKind = null);

public sealed record RunRejectRecord(long Id, string RunId, long BatchNumber, int RecordIndex, string Stage, string Reason, string? RecordJson, DateTimeOffset At);

public sealed record RunLogRecord(long Id, string RunId, DateTimeOffset At, string Level, string Message);

/// <summary>Progression enregistrée après un lot confirmé, en une transaction (compteurs + point de reprise + rejets).</summary>
public sealed record RunProgress(long Read, long Written, long Rejected, long Skipped, string? Checkpoint, IReadOnlyList<RunRejectRecord> NewRejects, IReadOnlyList<string>? NewDedupKeys = null);

/// <summary>Planification d'un flux (expression cron à 5 champs, fuseau horaire, nouvelles tentatives).</summary>
public sealed record ScheduleRecord(
    string FlowId,
    string Cron,
    string TimeZone,
    bool Enabled,
    int MaxRetries,
    int RetryDelaySeconds,
    long Version,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    DateTimeOffset? NextRunAt,
    string? LastRunId,
    int RetryAttempt,
    DateTimeOffset? RetryAt);

public sealed record AlertRecord(long Id, string FlowId, string? RunId, string Severity, string Message, DateTimeOffset CreatedAt, DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy);

public interface IScheduleRepository
{
    Task<IReadOnlyList<ScheduleRecord>> ListAsync(CancellationToken ct);

    Task<ScheduleRecord?> GetAsync(string flowId, CancellationToken ct);

    /// <summary>Crée (expectedVersion 0) ou modifie ; null si la version attendue ne correspond pas.</summary>
    Task<ScheduleRecord?> SaveAsync(ScheduleRecord record, long expectedVersion, CancellationToken ct);

    Task<bool> DeleteAsync(string flowId, CancellationToken ct);

    /// <summary>État du planificateur (ne change pas la version de configuration).</summary>
    Task SetStateAsync(string flowId, DateTimeOffset? nextRunAt, string? lastRunId, int retryAttempt, DateTimeOffset? retryAt, CancellationToken ct);
}

public interface IDedupRegistry
{
    /// <summary>Parmi les clés données, celles déjà enregistrées pour ce flux.</summary>
    Task<IReadOnlySet<string>> ExistingAsync(string flowId, IReadOnlyCollection<string> keyHashes, CancellationToken ct);

    Task RegisterAsync(string flowId, string runId, IReadOnlyCollection<string> keyHashes, DateTimeOffset at, CancellationToken ct);
}

public interface IAlertRepository
{
    Task<AlertRecord> RaiseAsync(string flowId, string? runId, string severity, string message, DateTimeOffset at, CancellationToken ct);

    Task<IReadOnlyList<AlertRecord>> ListAsync(bool openOnly, int limit, CancellationToken ct);

    Task<bool> AcknowledgeAsync(long id, string by, DateTimeOffset at, CancellationToken ct);
}

public sealed record RunQuery(string? FlowId, RunStatus? Status, int Page, int PageSize);

public interface IConnectionRepository
{
    Task<IReadOnlyList<ConnectionRecord>> ListAsync(CancellationToken ct);

    Task<ConnectionRecord?> GetAsync(string id, CancellationToken ct);

    /// <summary>Crée (expectedVersion = 0) ou met à jour si la version correspond ; null en cas de conflit.</summary>
    Task<ConnectionRecord?> SaveAsync(ConnectionRecord record, long expectedVersion, CancellationToken ct);

    Task<bool> DeleteAsync(string id, CancellationToken ct);
}

public interface IFlowRepository
{
    Task<IReadOnlyList<FlowRecord>> ListAsync(CancellationToken ct);

    Task<FlowRecord?> GetAsync(string id, CancellationToken ct);

    Task<FlowVersionRecord?> GetVersionAsync(string flowId, long version, CancellationToken ct);

    /// <summary>Enregistre une nouvelle version (immuable). expectedDraftVersion = 0 pour un nouveau flux. Null si conflit.</summary>
    Task<FlowRecord?> SaveAsync(string id, string name, string description, string definitionJson, long expectedDraftVersion, string by, DateTimeOffset at, CancellationToken ct);

    Task<FlowRecord?> PublishAsync(string flowId, long version, string by, DateTimeOffset at, CancellationToken ct);

    /// <summary>Flux utilisant une connexion (dans leur dernière version).</summary>
    Task<IReadOnlyList<string>> FlowsUsingConnectionAsync(string connectionId, CancellationToken ct);
}

public interface IRunRepository
{
    Task CreateAsync(RunRecord run, CancellationToken ct);

    Task<RunRecord?> GetAsync(string id, CancellationToken ct);

    Task<(IReadOnlyList<RunRecord> Items, int Total)> QueryAsync(RunQuery query, CancellationToken ct);

    /// <summary>Change le statut si le statut actuel est <paramref name="from"/> ; trace l'historique. Faux sinon.</summary>
    Task<bool> TransitionAsync(string id, RunStatus from, RunStatus to, string actor, string? reason, DateTimeOffset at, CancellationToken ct);

    Task SaveProgressAsync(string id, RunProgress progress, CancellationToken ct);

    Task SetErrorAsync(string id, string error, CancellationToken ct, string? kind = null);

    Task<IReadOnlyList<RunRejectRecord>> RejectsAsync(string runId, int limit, CancellationToken ct);

    Task LogAsync(string runId, DateTimeOffset at, string level, string message, CancellationToken ct);

    Task<IReadOnlyList<RunLogRecord>> LogsAsync(string runId, int limit, CancellationToken ct);

    Task<IReadOnlyList<RunRecord>> InStatusAsync(IReadOnlyList<RunStatus> statuses, CancellationToken ct);
}
