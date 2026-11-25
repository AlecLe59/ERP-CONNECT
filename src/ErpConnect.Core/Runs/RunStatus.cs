namespace ErpConnect.Core.Runs;

/// <summary>Les sept statuts d'exécution (ADR 0013). Les valeurs numériques sont persistées : ne jamais les renuméroter.</summary>
public enum RunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    SucceededWithWarnings = 3,
    Failed = 4,
    Suspended = 5,
    Cancelled = 6,
}

/// <summary>Origine d'un changement de statut.</summary>
public enum RunActor
{
    Engine = 0,
    Operator = 1,
    Administrator = 2,
}

public static class RunStatusExtensions
{
    public static bool IsTerminal(this RunStatus status) => status is
        RunStatus.Succeeded or RunStatus.SucceededWithWarnings or RunStatus.Failed or RunStatus.Cancelled;

    /// <summary>Libellé français affiché à l'utilisateur.</summary>
    public static string ToFrenchLabel(this RunStatus status) => status switch
    {
        RunStatus.Pending => "En attente",
        RunStatus.Running => "En cours",
        RunStatus.Succeeded => "Réussi",
        RunStatus.SucceededWithWarnings => "Réussi avec avertissements",
        RunStatus.Failed => "Échec",
        RunStatus.Suspended => "Suspendu",
        RunStatus.Cancelled => "Annulé",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
