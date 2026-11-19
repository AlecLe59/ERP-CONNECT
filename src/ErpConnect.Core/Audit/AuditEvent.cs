namespace ErpConnect.Core.Audit;

/// <summary>Entrée du journal d'audit chaîné. Hash = SHA-256(hash précédent || contenu canonique).</summary>
public sealed record AuditEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string ActorSid,
    string ActorName,
    string Action,
    string Target,
    string Outcome,
    string? DetailsJson,
    string CorrelationId,
    string PreviousHash,
    string Hash);

/// <summary>Données d'une entrée à ajouter (la séquence et les empreintes sont calculées par le journal).</summary>
public sealed record AuditEntry(
    string ActorSid,
    string ActorName,
    string Action,
    string Target,
    string Outcome,
    string? DetailsJson,
    string CorrelationId);

public static class AuditOutcomes
{
    public const string Success = "success";
    public const string Denied = "denied";
    public const string Failure = "failure";
}

public sealed record AuditVerification(bool Valid, long CheckedCount, long? FirstInvalidSequence, string? Reason);

public sealed record AuditQuery(int Page, int PageSize, string? Action, DateTimeOffset? From, DateTimeOffset? To);

public sealed record AuditPage(IReadOnlyList<AuditEvent> Items, long Total);
