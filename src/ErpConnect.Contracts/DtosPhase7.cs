namespace ErpConnect.Contracts;

#pragma warning disable CA1819 // Forme sérialisée.

/// <summary>Licence signée par l'éditeur (contenu affiché ; la signature a été vérifiée par le moteur).</summary>
public sealed record LicenseInfoDto(
    string LicenseId,
    string Customer,
    string Edition,
    string InstallationId,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    string[] Features,
    int? MaxPublishedFlows);

/// <summary>
/// state : evaluation | evaluationExpired | licensed | expired | invalid.
/// installationId : à transmettre à l'éditeur pour obtenir une licence (activation hors ligne).
/// canVerify : faux si cette version ne contient pas de clé d'éditeur (aucune licence importable).
/// </summary>
public sealed record LicenseStatusResult(
    string State,
    string InstallationId,
    DateTimeOffset? EvaluationEndsAt,
    LicenseInfoDto? License,
    string? Problem,
    DateTimeOffset? ImportedAt,
    string? ImportedBy,
    bool CanVerify,
    int PublishedFlows);

/// <summary>content : texte du fichier de licence (JSON signé, 16 Kio maximum).</summary>
public sealed record LicenseImportParams(string Content);
