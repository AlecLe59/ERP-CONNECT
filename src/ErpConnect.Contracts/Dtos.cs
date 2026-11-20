namespace ErpConnect.Contracts;

#pragma warning disable CA1819 // Les tableaux sont la forme naturelle des DTO sérialisés.

public sealed record EmptyParams;

public sealed record HelloParams(string ClientName, string ClientVersion, string ProtocolVersion);

public sealed record HelloResult(
    string SessionId,
    string ProtocolVersion,
    string ServerVersion,
    string UserSid,
    string UserName,
    string[] Roles,
    string[] Permissions);

public sealed record DatabaseStatusDto(string Name, bool Ok, int SchemaVersion);

public sealed record VaultStatusResult(bool Initialized, int? ActiveKeyVersion, int SecretCount, string Protection);

public sealed record SystemStatusResult(
    string State,
    string Version,
    long UptimeSeconds,
    long DataDiskFreeBytes,
    long DataDiskTotalBytes,
    DatabaseStatusDto[] Databases,
    VaultStatusResult Vault,
    DateTimeOffset ServerTime);

public sealed record SystemInfoResult(
    string InstallationId,
    string MachineName,
    string ServiceAccount,
    string DataDirectory,
    string LogDirectory,
    string OsDescription,
    DateTimeOffset InstalledAt);

public sealed record SettingsDto(
    int RunHeaderRetentionDays,
    int SucceededLogRetentionDays,
    int FailedLogRetentionDays,
    int RejectRetentionDays,
    int DeduplicationRetentionDays,
    string LogLevel,
    string Language,
    bool ScriptsEnabled = false);

public sealed record SettingsResult(SettingsDto Settings, long Version, DateTimeOffset UpdatedAt, string UpdatedBy);

public sealed record SettingsUpdateParams(SettingsDto Settings, long ExpectedVersion);

/// <summary>principalKind : "user" | "group" ; role : "administrator" | "designer" | "operator" | "reader" | "auditor".</summary>
public sealed record RoleAssignmentDto(
    long Id,
    string PrincipalSid,
    string PrincipalName,
    string PrincipalKind,
    string Role,
    string Scope,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed record ListAssignmentsResult(RoleAssignmentDto[] Items);

public sealed record AssignRoleParams(string PrincipalSid, string PrincipalName, string PrincipalKind, string Role);

public sealed record RevokeRoleParams(long AssignmentId);

public sealed record RevokeRoleResult(bool Revoked);

public sealed record ResolvePrincipalParams(string Name);

public sealed record ResolvePrincipalResult(bool Found, string? Sid, string? Name, string? Kind);

public sealed record AuditQueryParams(int Page, int PageSize, string? Action, DateTimeOffset? From, DateTimeOffset? To);

public sealed record AuditEventDto(
    long Sequence,
    DateTimeOffset Timestamp,
    string ActorSid,
    string ActorName,
    string Action,
    string Target,
    string Outcome,
    string? Details,
    string CorrelationId,
    string Hash);

public sealed record AuditQueryResult(AuditEventDto[] Items, long Total, int Page, int PageSize);

public sealed record AuditVerifyResult(bool Valid, long CheckedCount, long? FirstInvalidSequence, string? Reason);

public sealed record EventsSubscribeParams(string[] Topics);

public sealed record EventsSubscribeResult(string[] Subscribed);

public sealed record EngineStatusChangedPayload(string State, DateTimeOffset At);

public sealed record SettingsChangedPayload(long Version, string UpdatedBy);

public sealed record AuditAppendedPayload(long Sequence, string Action, string Outcome);

/// <summary>Contenu de error.data pour les erreurs métier.</summary>
public sealed record RpcErrorData(string Code, string CorrelationId, FieldErrorDto[]? Fields);

public sealed record FieldErrorDto(string Field, string Message);

#pragma warning restore CA1819
