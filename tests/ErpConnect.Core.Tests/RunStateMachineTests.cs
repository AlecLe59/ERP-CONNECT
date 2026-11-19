using ErpConnect.Core.Runs;

namespace ErpConnect.Core.Tests;

public class RunStateMachineTests
{
    // Table de référence du dossier de validation, section 2 (de, vers, acteurs autorisés).
    private static readonly (RunStatus From, RunStatus To, RunActor[] Actors)[] Expected =
    [
        (RunStatus.Pending, RunStatus.Running, [RunActor.Engine]),
        (RunStatus.Pending, RunStatus.Cancelled, [RunActor.Operator, RunActor.Administrator]),
        (RunStatus.Pending, RunStatus.Suspended, [RunActor.Engine, RunActor.Administrator]),
        (RunStatus.Running, RunStatus.Succeeded, [RunActor.Engine]),
        (RunStatus.Running, RunStatus.SucceededWithWarnings, [RunActor.Engine]),
        (RunStatus.Running, RunStatus.Failed, [RunActor.Engine]),
        (RunStatus.Running, RunStatus.Suspended, [RunActor.Operator, RunActor.Administrator, RunActor.Engine]),
        (RunStatus.Running, RunStatus.Cancelled, [RunActor.Operator, RunActor.Administrator]),
        (RunStatus.Suspended, RunStatus.Pending, [RunActor.Operator, RunActor.Administrator, RunActor.Engine]),
        (RunStatus.Suspended, RunStatus.Cancelled, [RunActor.Operator, RunActor.Administrator]),
        (RunStatus.Suspended, RunStatus.Failed, [RunActor.Engine]),
    ];

    [Fact]
    public void Exhaustive_matrix_matches_the_validated_table()
    {
        var checkedCount = 0;
        foreach (var from in Enum.GetValues<RunStatus>())
        {
            foreach (var to in Enum.GetValues<RunStatus>())
            {
                foreach (var actor in Enum.GetValues<RunActor>())
                {
                    var expected = Expected.Any(e => e.From == from && e.To == to && e.Actors.Contains(actor));
                    RunStateMachine.Check(from, to, actor).Allowed.ShouldBe(expected, $"{from} → {to} par {actor}");
                    checkedCount++;
                }
            }
        }

        checkedCount.ShouldBe(7 * 7 * 3);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.SucceededWithWarnings)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    public void Terminal_statuses_never_change(RunStatus terminal)
    {
        terminal.IsTerminal().ShouldBeTrue();
        foreach (var to in Enum.GetValues<RunStatus>())
        {
            var r = RunStateMachine.Check(terminal, to, RunActor.Administrator);
            r.Allowed.ShouldBeFalse();
            r.RefusalReason.ShouldNotBeNull().ShouldContain("terminal");
        }
    }

    [Fact]
    public void There_are_exactly_seven_statuses_and_four_terminal()
    {
        Enum.GetValues<RunStatus>().Length.ShouldBe(7);
        Enum.GetValues<RunStatus>().Count(s => s.IsTerminal()).ShouldBe(4);
        RunStatus.Cancelled.ToFrenchLabel().ShouldBe("Annulé");
    }

    [Fact]
    public void Persisted_numeric_values_are_stable()
    {
        ((int)RunStatus.Pending).ShouldBe(0);
        ((int)RunStatus.Cancelled).ShouldBe(6);
    }
}
