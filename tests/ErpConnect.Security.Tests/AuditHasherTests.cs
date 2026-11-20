using ErpConnect.Core.Audit;
using ErpConnect.Security.Audit;

namespace ErpConnect.Security.Tests;

public class AuditHasherTests
{
    private static List<AuditEvent> Chain(int n)
    {
        var list = new List<AuditEvent>();
        var prev = AuditHasher.GenesisHash;
        for (var i = 1; i <= n; i++)
        {
            var ts = new DateTimeOffset(2026, 11, 19, 10, 0, i, TimeSpan.Zero);
            var e = new AuditEntry("S-1-5-21-1", "u", "settings.update", "t", "success", $"{{\"i\":{i}}}", $"c{i}");
            var hash = AuditHasher.Compute(prev, i, ts, e);
            list.Add(new AuditEvent(i, ts, e.ActorSid, e.ActorName, e.Action, e.Target, e.Outcome, e.DetailsJson, e.CorrelationId, prev, hash));
            prev = hash;
        }

        return list;
    }

    [Fact]
    public void Valid_chain_verifies() => AuditHasher.Verify(Chain(10)).ShouldBe(new AuditVerification(true, 10, null, null));

    [Fact]
    public void Modified_entry_is_detected()
    {
        var chain = Chain(10);
        chain[4] = chain[4] with { Outcome = "denied" };
        var v = AuditHasher.Verify(chain);
        v.Valid.ShouldBeFalse();
        v.FirstInvalidSequence.ShouldBe(5);
    }

    [Fact]
    public void Deleted_entry_is_detected()
    {
        var chain = Chain(10);
        chain.RemoveAt(3);
        AuditHasher.Verify(chain).FirstInvalidSequence.ShouldBe(4);
    }

    [Fact]
    public void Field_boundaries_are_unambiguous()
    {
        var ts = DateTimeOffset.UnixEpoch;
        var a = AuditHasher.Compute(AuditHasher.GenesisHash, 1, ts, new AuditEntry("ab", "c", "x", "t", "o", null, "k"));
        var b = AuditHasher.Compute(AuditHasher.GenesisHash, 1, ts, new AuditEntry("a", "bc", "x", "t", "o", null, "k"));
        a.ShouldNotBe(b);
    }
}
