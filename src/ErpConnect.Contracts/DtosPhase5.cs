namespace ErpConnect.Contracts;

/// <summary>Planification d'un flux. cron : 5 champs (minute heure jour mois jour-semaine). timeZone : IANA ou Windows.</summary>
public sealed record ScheduleDto(
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

public sealed record SchedulesListResult(ScheduleDto[] Schedules);

public sealed record ScheduleSaveParams(string FlowId, string Cron, string TimeZone, bool Enabled, int MaxRetries, int RetryDelaySeconds, long ExpectedVersion);

/// <summary>severity : warning | error.</summary>
public sealed record AlertDto(long Id, string FlowId, string? RunId, string Severity, string Message, DateTimeOffset CreatedAt, DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy);

public sealed record AlertsListParams(bool OpenOnly, int Limit);

public sealed record AlertsListResult(AlertDto[] Alerts);

public sealed record AlertAcknowledgeParams(long Id);

public sealed record AlertAcknowledgeResult(bool Acknowledged);

public sealed record AlertRaisedPayload(long Id, string FlowId, string Severity, string Message);
