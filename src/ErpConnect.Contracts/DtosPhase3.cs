namespace ErpConnect.Contracts;

#pragma warning disable CA1819 // Les tableaux sont la forme naturelle des DTO sérialisés.

public sealed record KeyValueDto(string Key, string? Value);

/// <summary>type : text | number | boolean | choice | url | path.</summary>
public sealed record ConfigFieldDto(string Name, string Label, string Type, bool Required, bool Secret, string? Default, string[] Choices, string? Help);

public sealed record ConnectorDto(string Id, string Name, string Version, string Description, string[] Capabilities, ConfigFieldDto[] ConfigFields);

public sealed record FormatDto(string Id, ConfigFieldDto[] Options);

public sealed record ConnectorsListResult(ConnectorDto[] Connectors, FormatDto[] Formats);

/// <summary>secretFields : noms des champs secrets renseignés. Leur valeur n'est jamais renvoyée.</summary>
public sealed record ConnectionDto(
    string Id,
    string Name,
    string ConnectorId,
    KeyValueDto[] Settings,
    string[] SecretFields,
    long Version,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record ConnectionsListResult(ConnectionDto[] Connections);

/// <summary>Un secret vide ou absent conserve la valeur enregistrée ; clearSecrets efface. expectedVersion = 0 pour créer.</summary>
public sealed record ConnectionSaveParams(
    string? Id,
    string Name,
    string ConnectorId,
    KeyValueDto[] Settings,
    [property: Secret] KeyValueDto[] Secrets,
    string[] ClearSecrets,
    long ExpectedVersion);

public sealed record IdParams(string Id);

public sealed record DeleteResult(bool Deleted);

public sealed record ConnectionTestResultDto(bool Success, string Message, long DurationMs);

public sealed record SourceSpecDto(string ConnectionId, string Resource, KeyValueDto[] Options);

/// <summary>mode : createNew | overwrite.</summary>
public sealed record TargetSpecDto(string ConnectionId, string Resource, string Mode, KeyValueDto[] Options);

public sealed record FieldMapDto(string Target, string Expression);

/// <summary>mode : identity | explicit.</summary>
public sealed record MappingSpecDto(string Mode, FieldMapDto[] Fields);

/// <summary>Bloc de transformation : filter, set, rename, remove ou validate.</summary>
public sealed record StepSpecDto(string Kind, string? Field, string? Expression, string? Target, string? Message);

/// <summary>dedupKey : expression de clé métier ; un enregistrement dont la clé a déjà été transmise par ce flux est ignoré.</summary>
public sealed record FlowDefinitionDto(SourceSpecDto Source, TargetSpecDto Target, MappingSpecDto Mapping, int BatchSize, int MaxRejects, StepSpecDto[]? Steps = null, string? DedupKey = null);

public sealed record FlowSummaryDto(string Id, string Name, string Description, long DraftVersion, long? PublishedVersion, DateTimeOffset UpdatedAt, string UpdatedBy);

public sealed record FlowsListResult(FlowSummaryDto[] Flows);

/// <summary>version absente = dernière version enregistrée.</summary>
public sealed record FlowGetParams(string Id, long? Version);

public sealed record FlowDetailResult(FlowSummaryDto Flow, long Version, FlowDefinitionDto Definition, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? PublishedAt);

public sealed record FlowSaveParams(string? Id, string Name, string Description, FlowDefinitionDto Definition, long ExpectedDraftVersion);

public sealed record FlowPublishParams(string Id, long Version);

/// <summary>useDraft : exécuter la dernière version enregistrée (test) plutôt que la version publiée.</summary>
public sealed record RunStartParams(string FlowId, bool UseDraft);

/// <summary>status : pending | running | succeeded | succeededWithWarnings | failed | suspended | cancelled.</summary>
public sealed record RunDto(
    string Id,
    string FlowId,
    long FlowVersion,
    string Status,
    string StatusLabel,
    string TriggeredBy,
    string? ParentRunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long ReadCount,
    long WrittenCount,
    long RejectedCount,
    long SkippedCount,
    string? Error,
    string? Trigger,
    string? ErrorKind = null);

public sealed record RunsListParams(string? FlowId, string? Status, int Page, int PageSize);

public sealed record RunsListResult(RunDto[] Items, int Total, int Page, int PageSize);

public sealed record RunRejectDto(long BatchNumber, int RecordIndex, string Stage, string Reason, string? RecordJson, DateTimeOffset At);

public sealed record RunLogDto(DateTimeOffset At, string Level, string Message);

public sealed record RunDetailResult(RunDto Run, RunRejectDto[] Rejects, RunLogDto[] Logs);

public sealed record RunCancelResult(bool Accepted);

public sealed record RunChangedPayload(string RunId, string FlowId, string Status);

/// <summary>Aperçu d'une définition (même non enregistrée) sur un échantillon de la source, sans écriture.</summary>
public sealed record FlowPreviewParams(FlowDefinitionDto Definition, int Limit);

/// <summary>Sort : output, filtered ou rejected.</summary>
public sealed record PreviewRowDto(string SourceJson, string? OutputJson, string Fate, string? Reason);

public sealed record FlowPreviewResult(PreviewRowDto[] Rows, string[] SourceFields);

public sealed record FunctionDto(string Name, string Signature, string Help);

public sealed record FunctionsListResult(FunctionDto[] Functions);

public sealed record ConnectionDiscoverParams(string Id, string? Resource);

public sealed record DiscoveredFieldDto(string Name, string Type, DiscoveredFieldDto[] Children);

public sealed record ConnectionDiscoverResult(string[] Resources, DiscoveredFieldDto[] Fields);
