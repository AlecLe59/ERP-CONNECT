using ErpConnect.Contracts;
using ErpConnect.Tools.ContractGen;

namespace ErpConnect.Contracts.Tests;

public class ContractSnapshotTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ErpConnect.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Racine du dépôt introuvable.");
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    // Tout changement du contrat non accompagné d'une mise à jour de l'instantané fait échouer la CI.
    [Fact]
    public void Committed_snapshot_matches_the_contract()
    {
        var committed = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ErpConnect.Contracts", "contract.snapshot.json"));
        Normalize(committed).ShouldBe(ContractModel.Snapshot(),
            "Le contrat a changé : relancez `dotnet run --project tools/ErpConnect.Tools.ContractGen` et faites revoir l'instantané.");
    }

    [Fact]
    public void Generated_typescript_is_up_to_date()
    {
        var committed = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "packages", "contracts", "src", "generated.ts"));
        Normalize(committed).ShouldBe(ContractModel.TypeScript());
    }

    [Fact]
    public void Method_list_is_exactly_the_validated_one()
    {
        RpcMethods.All.Select(m => m.Name).ShouldBe(
        [
            "session.hello", "system.getStatus", "system.getInfo", "settings.get", "settings.update", "users.listAssignments",
            "users.assignRole", "users.revokeRole", "users.resolvePrincipal", "audit.query", "audit.verifyChain", "vault.getStatus",
            "events.subscribe", "events.unsubscribe",
            "connectors.list", "connections.list", "connections.save", "connections.delete", "connections.test",
            "flows.list", "flows.get", "flows.save", "flows.publish", "runs.start", "runs.list", "runs.get", "runs.cancel",
            "connections.discover", "flows.preview", "flows.functions",
            "runs.resume", "schedules.list", "schedules.save", "schedules.delete", "alerts.list", "alerts.acknowledge",
            "scripts.test", "scripts.limits",
        ]);
        RpcMethods.All.Select(m => m.Name).ShouldBeUnique();
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.7", true)]
    [InlineData("2.0", false)]
    [InlineData("0.9", false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    public void Version_negotiation(string version, bool compatible) => Protocol.IsCompatible(version).ShouldBe(compatible);

    [Fact]
    public void Error_codes_match_the_validated_table()
    {
        RpcErrorCodes.Unauthenticated.ShouldBe(-32001);
        RpcErrorCodes.Forbidden.ShouldBe(-32002);
        RpcErrorCodes.NotFound.ShouldBe(-32003);
        RpcErrorCodes.Conflict.ShouldBe(-32004);
        RpcErrorCodes.ValidationFailed.ShouldBe(-32005);
        RpcErrorCodes.ProtocolVersionMismatch.ShouldBe(-32006);
        RpcErrorCodes.EngineUnavailable.ShouldBe(-32010);
        RpcErrorCodes.EntitlementDenied.ShouldBe(-32011);
    }
}
