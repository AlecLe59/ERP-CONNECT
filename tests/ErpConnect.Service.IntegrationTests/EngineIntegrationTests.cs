using System.Text.Json.Nodes;
using ErpConnect.Contracts;
using ErpConnect.Core.Authorization;
using ErpConnect.Tests.Engine;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Service.IntegrationTests;

public sealed class EngineIntegrationTests : IAsyncLifetime
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
    public async Task Machine_administrators_group_is_administrator_on_first_start()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin, hello: false);
        var hello = (await c.HelloAsync()).Result();
        hello["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["administrator"]);
        hello["permissions"]!.AsArray().Count.ShouldBe(Permissions.All.Count);
    }

    [Fact]
    public async Task Status_reflects_the_real_engine()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        var s = (await c.CallAsync("system.getStatus")).Result();
        s["state"]!.GetValue<string>().ShouldBe("running");
        s["version"]!.GetValue<string>().ShouldBe("0.2.0-test");
        s["databases"]!.AsArray().Select(d => (d!["name"]!.GetValue<string>(), d["ok"]!.GetValue<bool>(), d["schemaVersion"]!.GetValue<int>()))
            .ShouldBe([("config", true, 1), ("runs", true, 1), ("audit", true, 1)]);
        s["vault"]!["initialized"]!.GetValue<bool>().ShouldBeTrue();
        s["vault"]!["activeKeyVersion"]!.GetValue<int>().ShouldBe(1);
        s["dataDiskTotalBytes"]!.GetValue<long>().ShouldBeGreaterThan(0);

        var info = (await c.CallAsync("system.getInfo")).Result();
        info["installationId"]!.GetValue<string>().ShouldBe(_engine.Runtime.Installation.Id);
        info["dataDirectory"]!.GetValue<string>().ShouldBe(Path.Combine(_dir.Path, "data"));
    }

    [Fact]
    public async Task Settings_are_validated_versioned_and_survive_a_restart()
    {
        await using (var c = await _engine.ConnectAsync(Identities.Admin))
        {
            var current = (await c.CallAsync("settings.get")).Result();
            var version = current["version"]!.GetValue<long>();
            var settings = current["settings"]!.Deserialize<SettingsDto>(ErpConnect.Rpc.Dispatch.RpcDispatcher.SerializerOptions)!;

            var invalid = await c.CallAsync("settings.update", new SettingsUpdateParams(settings with { RejectRetentionDays = 3 }, version));
            invalid.ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
            invalid["error"]!["data"]!["fields"]![0]!["field"]!.GetValue<string>().ShouldBe("RejectRetentionDays");

            var ok = (await c.CallAsync("settings.update", new SettingsUpdateParams(settings with { FailedLogRetentionDays = 180, Language = "en-US" }, version))).Result();
            ok["version"]!.GetValue<long>().ShouldBe(version + 1);

            (await c.CallAsync("settings.update", new SettingsUpdateParams(settings, version))).ErrorCode().ShouldBe(RpcErrorCodes.Conflict);
        }

        // Redémarrage du moteur sur les mêmes données.
        await _engine.DisposeAsync();
        _engine = await EngineHarness.StartAsync(_dir.Path);
        await using var c2 = await _engine.ConnectAsync(Identities.Admin);
        var reread = (await c2.CallAsync("settings.get")).Result();
        reread["settings"]!["failedLogRetentionDays"]!.GetValue<int>().ShouldBe(180);
        reread["settings"]!["language"]!.GetValue<string>().ShouldBe("en-US");
        reread["updatedBy"]!.GetValue<string>().ShouldBe(Identities.Admin.UserName);
        (await c2.CallAsync("users.listAssignments")).Result()["items"]!.AsArray().Count.ShouldBe(1); // pas de doublon admin
    }

    [Fact]
    public async Task Operator_group_member_gets_the_restricted_interface()
    {
        var member = new CallerIdentity("S-1-5-21-1000-1000-1000-3001", @"PC\operateur", [Identities.OperatorsGroup]);
        await using var admin = await _engine.ConnectAsync(Identities.Admin);
        (await admin.CallAsync("users.assignRole", new AssignRoleParams(Identities.OperatorsGroup, @"PC\ERP Connect Opérateurs", "group", "operator"))).Result();

        await using var op = await _engine.ConnectAsync(member, hello: false);
        var hello = (await op.HelloAsync()).Result();
        hello["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["operator"]);
        hello["permissions"]!.AsArray().Select(p => p!.GetValue<string>())
            .ShouldBe(["dashboard.read", "flows.read", "flows.run", "runs.cancel", "settings.read"]);
        (await op.CallAsync("system.getStatus"))["result"].ShouldNotBeNull();
        (await op.CallAsync("settings.get"))["result"].ShouldNotBeNull();
        (await op.CallAsync("users.listAssignments")).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
        (await op.CallAsync("audit.query", new AuditQueryParams(1, 10, null, null, null))).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Revoking_a_role_applies_immediately_and_is_audited()
    {
        var user = Identities.ForUser("4001");
        await using var admin = await _engine.ConnectAsync(Identities.Admin);
        var a = (await admin.CallAsync("users.assignRole", new AssignRoleParams(user.UserSid, user.UserName, "user", "reader"))).Result();
        await using var reader = await _engine.ConnectAsync(user);
        (await reader.CallAsync("settings.get"))["result"].ShouldNotBeNull();

        (await admin.CallAsync("users.revokeRole", new RevokeRoleParams(a["id"]!.GetValue<long>()))).Result()["revoked"]!.GetValue<bool>().ShouldBeTrue();
        (await reader.CallAsync("settings.get")).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);

        var audit = (await admin.CallAsync("audit.query", new AuditQueryParams(1, 50, null, null, null))).Result()["items"]!.AsArray();
        audit.Select(e => (e!["action"]!.GetValue<string>(), e["outcome"]!.GetValue<string>()))
            .ShouldContain(("users.revokeRole", "success"));
        audit.Select(e => (e!["action"]!.GetValue<string>(), e["outcome"]!.GetValue<string>()))
            .ShouldContain(("settings.get", "denied"));
    }

    // S14
    [Fact]
    public async Task Last_administrator_cannot_be_removed()
    {
        await using var admin = await _engine.ConnectAsync(Identities.Admin);
        var items = (await admin.CallAsync("users.listAssignments")).Result()["items"]!.AsArray();
        var id = items.Single()!["id"]!.GetValue<long>();
        var r = await admin.CallAsync("users.revokeRole", new RevokeRoleParams(id));
        r.ErrorCode().ShouldBe(RpcErrorCodes.Conflict);
        r["error"]!["message"]!.GetValue<string>().ShouldContain("dernier Administrateur");

        // Avec un second administrateur, le retrait du premier devient possible.
        var other = Identities.ForUser("5001");
        (await admin.CallAsync("users.assignRole", new AssignRoleParams(other.UserSid, other.UserName, "user", "administrator"))).Result();
        (await admin.CallAsync("users.revokeRole", new RevokeRoleParams(id))).Result()["revoked"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Invalid_role_parameters_are_rejected()
    {
        await using var admin = await _engine.ConnectAsync(Identities.Admin);
        (await admin.CallAsync("users.assignRole", new AssignRoleParams("not-a-sid", "x", "user", "reader"))).ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        (await admin.CallAsync("users.assignRole", new AssignRoleParams("S-1-5-21-1-2-3-4", "x", "user", "root"))).ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        (await admin.CallAsync("users.assignRole", new AssignRoleParams("S-1-5-21-1-2-3-4", "x", "robot", "reader"))).ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        (await admin.CallAsync("users.revokeRole", new RevokeRoleParams(999))).ErrorCode().ShouldBe(RpcErrorCodes.NotFound);
    }

    [Fact]
    public async Task Principal_resolution()
    {
        _engine.Principals.Known["PC\\compta"] = new ErpConnect.Core.Abstractions.ResolvedPrincipal("S-1-5-21-7-7-7-1100", "PC\\compta", PrincipalKind.Group);
        await using var admin = await _engine.ConnectAsync(Identities.Admin);
        var found = (await admin.CallAsync("users.resolvePrincipal", new ResolvePrincipalParams("PC\\compta"))).Result();
        found["found"]!.GetValue<bool>().ShouldBeTrue();
        found["kind"]!.GetValue<string>().ShouldBe("group");
        (await admin.CallAsync("users.resolvePrincipal", new ResolvePrincipalParams("absent"))).Result()["found"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Audit_chain_is_valid_after_real_activity()
    {
        await using var admin = await _engine.ConnectAsync(Identities.Admin);
        for (var i = 0; i < 5; i++)
        {
            await admin.CallAsync("users.assignRole", new AssignRoleParams($"S-1-5-21-1-2-3-{100 + i}", $"u{i}", "user", "reader"));
        }

        var v = (await admin.CallAsync("audit.verifyChain")).Result();
        v["valid"]!.GetValue<bool>().ShouldBeTrue();
        v["checkedCount"]!.GetValue<long>().ShouldBeGreaterThanOrEqualTo(6);
        var page = (await admin.CallAsync("audit.query", new AuditQueryParams(1, 2, "users.assignRole", null, null))).Result();
        page["items"]!.AsArray().Count.ShouldBe(2);
        page["total"]!.GetValue<long>().ShouldBe(6); // 5 + l'attribution initiale
        (await admin.CallAsync("audit.query", new AuditQueryParams(0, 500, null, null, null))).ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Subscribed_clients_receive_settings_changed()
    {
        await using var watcher = await _engine.ConnectAsync(Identities.Admin);
        (await watcher.CallAsync("events.subscribe", new EventsSubscribeParams(["settings.changed"]))).Result()["subscribed"]!.AsArray()
            .Select(x => x!.GetValue<string>()).ShouldBe(["settings.changed"]);
        (await watcher.CallAsync("events.subscribe", new EventsSubscribeParams(["nope"]))).ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);

        await using var editor = await _engine.ConnectAsync(Identities.Admin);
        var current = (await editor.CallAsync("settings.get")).Result();
        var settings = current["settings"]!.Deserialize<SettingsDto>(ErpConnect.Rpc.Dispatch.RpcDispatcher.SerializerOptions)!;
        await editor.CallAsync("settings.update", new SettingsUpdateParams(settings with { LogLevel = "Debug" }, current["version"]!.GetValue<long>()));

        var n = await watcher.ReadNotificationAsync(3000);
        n.ShouldNotBeNull();
        n["method"]!.GetValue<string>().ShouldBe("settings.changed");
        n["params"]!["updatedBy"]!.GetValue<string>().ShouldBe(Identities.Admin.UserName);
    }

    [Fact]
    public async Task Subscription_requires_the_topic_permission()
    {
        var op = new CallerIdentity("S-1-5-21-1000-1000-1000-3002", @"PC\op2", [Identities.OperatorsGroup]);
        await using (var admin = await _engine.ConnectAsync(Identities.Admin))
        {
            await admin.CallAsync("users.assignRole", new AssignRoleParams(Identities.OperatorsGroup, "ops", "group", "operator"));
        }

        await using var c = await _engine.ConnectAsync(op);
        (await c.CallAsync("events.subscribe", new EventsSubscribeParams(["audit.appended"]))).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
        (await c.CallAsync("events.subscribe", new EventsSubscribeParams(["engine.statusChanged"])))["result"].ShouldNotBeNull();
    }
}

internal static class JsonNodeExtensions
{
    public static T? Deserialize<T>(this JsonNode node, System.Text.Json.JsonSerializerOptions options) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(node.ToJsonString(), options);
}
