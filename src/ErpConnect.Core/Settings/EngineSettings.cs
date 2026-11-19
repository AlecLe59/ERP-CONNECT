namespace ErpConnect.Core.Settings;

/// <summary>Paramètres généraux de la Phase 2. Durées en jours.</summary>
public sealed record EngineSettings
{
    public const int MinimumRetentionDays = 7;

    public int RunHeaderRetentionDays { get; init; } = 396; // 13 mois
    public int SucceededLogRetentionDays { get; init; } = 30;
    public int FailedLogRetentionDays { get; init; } = 90;
    public int RejectRetentionDays { get; init; } = 90;
    public int DeduplicationRetentionDays { get; init; } = 396;
    public string LogLevel { get; init; } = "Information";
    public string Language { get; init; } = "fr-FR";

    /// <summary>Blocs « script » autorisés (ADR 0008 : désactivés par défaut, activation par un administrateur, tracée).</summary>
    public bool ScriptsEnabled { get; init; }

    public static IReadOnlyList<string> AllowedLogLevels { get; } = ["Debug", "Information", "Warning", "Error"];

    public static IReadOnlyList<string> AllowedLanguages { get; } = ["fr-FR", "en-US"];

    /// <summary>Renvoie la liste des champs invalides (vide si valide).</summary>
    public IReadOnlyList<FieldError> Validate()
    {
        var errors = new List<FieldError>();
        void Retention(string name, int value)
        {
            if (value < MinimumRetentionDays)
            {
                errors.Add(new FieldError(name, $"Minimum {MinimumRetentionDays} jours."));
            }
            else if (value > 3650)
            {
                errors.Add(new FieldError(name, "Maximum 3650 jours."));
            }
        }

        Retention(nameof(RunHeaderRetentionDays), RunHeaderRetentionDays);
        Retention(nameof(SucceededLogRetentionDays), SucceededLogRetentionDays);
        Retention(nameof(FailedLogRetentionDays), FailedLogRetentionDays);
        Retention(nameof(RejectRetentionDays), RejectRetentionDays);
        Retention(nameof(DeduplicationRetentionDays), DeduplicationRetentionDays);
        if (!AllowedLogLevels.Contains(LogLevel, StringComparer.Ordinal))
        {
            errors.Add(new FieldError(nameof(LogLevel), "Valeur non reconnue."));
        }

        if (!AllowedLanguages.Contains(Language, StringComparer.Ordinal))
        {
            errors.Add(new FieldError(nameof(Language), "Valeur non reconnue."));
        }

        return errors;
    }
}

public sealed record FieldError(string Field, string Message);

/// <summary>Paramètres avec leur numéro de version (concurrence optimiste).</summary>
public sealed record VersionedSettings(EngineSettings Settings, long Version, DateTimeOffset UpdatedAt, string UpdatedBy);
