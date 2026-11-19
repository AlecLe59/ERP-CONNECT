using System.Text.Json.Nodes;
using ErpConnect.Contracts;
using ErpConnect.Core.Authorization;
using ErpConnect.Tests.Engine;
using ErpConnect.Tests.Shared;

namespace ErpConnect.SecurityTests;

/// <summary>S3 : chaque méthode RPC × chaque rôle, conforme au tableau des permissions ; chaque refus est audité.</summary>
public sealed class PermissionMatrixTests : IAsyncLifetime
{
    private readonly TempDirectory _dir = new();
    private EngineHarness _engine = null!;

    public async Task InitializeAsync() => _engine = await EngineHarness.StartAsync(_dir.Path);

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _dir.Dispose();
    }

    [Fact]
    public async Task S3_every_method_for_every_role()
    {
        await using var admin = await _engine.ConnectAsync(Identities.Admin);
        var settings = (await admin.CallAsync("settings.get")).Result();
        var users = new Dictionary<Role, CallerIdentity>();
        var rid = 6000;
        foreach (var role in Enum.GetValues<Role>())
        {
            var who = Identities.ForUser((rid++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            (await admin.CallAsync("users.assignRole", new AssignRoleParams(who.UserSid, who.UserName, "user", role.ToString().ToLowerInvariant())))
                .Result();
            users[role] = who;
        }

        var calls = new Dictionary<string, Func<object?>>
        {
            ["system.getStatus"] = () => null,
            ["system.getInfo"] = () => null,
            ["settings.get"] = () => null,
            ["settings.update"] = () => JsonNode.Parse($$"""{"settings":{{settings["settings"]!.ToJsonString()}},"expectedVersion":999}"""),
            ["users.listAssignments"] = () => null,
            ["users.assignRole"] = () => new AssignRoleParams("S-1-5-21-9-9-9-9", "x", "user", "reader"),
            ["users.revokeRole"] = () => new RevokeRoleParams(999_999),
            ["users.resolvePrincipal"] = () => new ResolvePrincipalParams("x"),
            ["audit.query"] = () => new AuditQueryParams(1, 10, null, null, null),
            ["audit.verifyChain"] = () => null,
            ["vault.getStatus"] = () => null,
            ["connectors.list"] = () => null,
            ["connections.list"] = () => null,
            ["connections.save"] = () => new ConnectionSaveParams(null, "", "inconnu", [], [], [], 0),
            ["connections.delete"] = () => new IdParams("absent"),
            ["connections.test"] = () => new IdParams("absent"),
            ["flows.list"] = () => null,
            ["flows.get"] = () => new FlowGetParams("absent", null),
            ["flows.save"] = () => new FlowSaveParams(null, "", "", new FlowDefinitionDto(new("", "", []), new("", "", "createNew", []), new("identity", []), 100, 10), 0),
            ["flows.publish"] = () => new FlowPublishParams("absent", 1),
            ["runs.start"] = () => new RunStartParams("absent", false),
            ["runs.list"] = () => new RunsListParams(null, null, 1, 10),
            ["runs.get"] = () => new IdParams("absent"),
            ["runs.cancel"] = () => new IdParams("absent"),
            ["connections.discover"] = () => new ConnectionDiscoverParams("absent", null),
            ["flows.preview"] = () => new FlowPreviewParams(new FlowDefinitionDto(new("absent", "", []), new("", "", "createNew", []), new("identity", []), 100, 10), 5),
            ["flows.functions"] = () => null,
            ["runs.resume"] = () => new IdParams("absent"),
            ["schedules.list"] = () => null,
            ["schedules.save"] = () => new ScheduleSaveParams("absent", "0 6 * * *", "Europe/Paris", false, 0, 60, 0),
            ["schedules.delete"] = () => new IdParams("absent"),
            ["alerts.list"] = () => new AlertsListParams(true, 10),
            ["alerts.acknowledge"] = () => new AlertAcknowledgeParams(999_999),
        };

        var checkedPairs = 0;
        var expectedDenials = 0;
        foreach (var (role, who) in users)
        {
            await using var c = await _engine.ConnectAsync(who);
            foreach (var m in RpcMethods.All.Where(m => m.PermissionMode == PermissionMode.Required))
            {
                calls.ShouldContainKey(m.Name, "Méthode ajoutée au contrat sans cas de test S3.");
                var response = await c.CallAsync(m.Name, calls[m.Name]());
                var allowed = RolePermissions.For(role).Contains(m.Permission!);
                var forbidden = response["error"]?["code"]?.GetValue<int>() == RpcErrorCodes.Forbidden;
                forbidden.ShouldBe(!allowed, $"{role} / {m.Name} : {response.ToJsonString()}");
                if (!allowed)
                {
                    expectedDenials++;
                }

                checkedPairs++;
            }
        }

        checkedPairs.ShouldBe(5 * RpcMethods.All.Count(m => m.PermissionMode == PermissionMode.Required));
        var denied = (await admin.CallAsync("audit.query", new AuditQueryParams(1, 200, null, null, null))).Result()["items"]!.AsArray()
            .Count(e => e!["outcome"]!.GetValue<string>() == "denied" && e["target"]!.GetValue<string>() == "rpc");
        denied.ShouldBe(expectedDenials);
    }

    [Fact]
    public async Task S3_account_without_role_cannot_even_open_a_session()
    {
        await using var c = await _engine.ConnectAsync(Identities.NoRole, hello: false);
        (await c.HelloAsync()).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
        (await c.CallAsync("system.getStatus")).ErrorCode().ShouldBe(RpcErrorCodes.Unauthenticated);
    }
}
