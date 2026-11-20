namespace ErpConnect.Core.Runs;

/// <summary>Résultat d'une demande de transition.</summary>
public sealed record TransitionResult(bool Allowed, string? RefusalReason)
{
    public static TransitionResult Ok { get; } = new(true, null);

    public static TransitionResult Refused(string reason) => new(false, reason);
}

/// <summary>
/// Machine à états des exécutions (dossier de validation, section 2).
/// Toute transition absente de la table est refusée.
/// </summary>
public static class RunStateMachine
{
    private static readonly Dictionary<(RunStatus From, RunStatus To), RunActor[]> Transitions =
        new Dictionary<(RunStatus, RunStatus), RunActor[]>
        {
            [(RunStatus.Pending, RunStatus.Running)] = [RunActor.Engine],
            [(RunStatus.Pending, RunStatus.Cancelled)] = [RunActor.Operator, RunActor.Administrator],
            [(RunStatus.Pending, RunStatus.Suspended)] = [RunActor.Engine, RunActor.Administrator],
            [(RunStatus.Running, RunStatus.Succeeded)] = [RunActor.Engine],
            [(RunStatus.Running, RunStatus.SucceededWithWarnings)] = [RunActor.Engine],
            [(RunStatus.Running, RunStatus.Failed)] = [RunActor.Engine],
            [(RunStatus.Running, RunStatus.Suspended)] = [RunActor.Operator, RunActor.Administrator, RunActor.Engine],
            [(RunStatus.Running, RunStatus.Cancelled)] = [RunActor.Operator, RunActor.Administrator],
            [(RunStatus.Suspended, RunStatus.Pending)] = [RunActor.Operator, RunActor.Administrator, RunActor.Engine],
            [(RunStatus.Suspended, RunStatus.Cancelled)] = [RunActor.Operator, RunActor.Administrator],
            [(RunStatus.Suspended, RunStatus.Failed)] = [RunActor.Engine],
        };

    /// <summary>Durée maximale de suspension par défaut, au-delà de laquelle le moteur passe l'exécution en Échec.</summary>
    public static TimeSpan DefaultMaxSuspension { get; } = TimeSpan.FromDays(7);

    public static IEnumerable<(RunStatus From, RunStatus To, IReadOnlyList<RunActor> Actors)> AllowedTransitions =>
        Transitions.Select(t => (t.Key.From, t.Key.To, (IReadOnlyList<RunActor>)t.Value));

    public static TransitionResult Check(RunStatus from, RunStatus to, RunActor actor)
    {
        if (from.IsTerminal())
        {
            return TransitionResult.Refused($"Le statut {from.ToFrenchLabel()} est terminal.");
        }

        if (!Transitions.TryGetValue((from, to), out var actors))
        {
            return TransitionResult.Refused($"Transition {from.ToFrenchLabel()} → {to.ToFrenchLabel()} non autorisée.");
        }

        return actors.Contains(actor)
            ? TransitionResult.Ok
            : TransitionResult.Refused($"Transition {from.ToFrenchLabel()} → {to.ToFrenchLabel()} non autorisée pour {actor}.");
    }
}
