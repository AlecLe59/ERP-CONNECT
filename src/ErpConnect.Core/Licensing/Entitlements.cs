namespace ErpConnect.Core.Licensing;

/// <summary>Fonctions soumises à licence.</summary>
public static class Features
{
    /// <summary>Lancer une exécution (manuelle ou planifiée) : licence valide ou période d'évaluation en cours.</summary>
    public const string Run = "run";

    public const string Scheduler = "scheduler";

    public const string Scripts = "scripts";

    /// <summary>Publier un flux de plus (plafond maxPublishedFlows).</summary>
    public const string PublishFlow = "publishFlow";
}

public sealed record EntitlementDecision(bool Allowed, string? Reason)
{
    public static EntitlementDecision Yes { get; } = new(true, null);

    public static EntitlementDecision No(string reason) => new(false, reason);
}

/// <summary>Droits d'usage de l'installation. Une absence d'implémentation (tests du noyau) vaut tout autorisé.</summary>
public interface IEntitlements
{
    Task<EntitlementDecision> CheckAsync(string feature, CancellationToken ct);
}

public sealed record StoredLicense(string Content, string LicenseId, DateTimeOffset ImportedAt, string ImportedBy);

public interface ILicenseStore
{
    Task<StoredLicense?> GetAsync(CancellationToken ct);

    Task SaveAsync(StoredLicense license, CancellationToken ct);

    Task<bool> DeleteAsync(CancellationToken ct);
}
