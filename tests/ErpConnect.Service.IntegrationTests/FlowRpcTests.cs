using System.Text.Json.Nodes;
using ErpConnect.Contracts;
using ErpConnect.Core.Authorization;
using ErpConnect.Tests.Engine;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Service.IntegrationTests;

/// <summary>Phase 3 de bout en bout par le pipe : connexions, flux, exécution, droits, secrets jamais renvoyés.</summary>
public sealed class FlowRpcTests : IAsyncLifetime
{
    private readonly TempDirectory _dir = new();
    private EngineHarness _engine = null!;

    public async Task InitializeAsync() => _engine = await EngineHarness.StartAsync(_dir.Path);

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _dir.Dispose();
    }

    private string Folder(string name)
    {
        var p = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static ConnectionSaveParams Local(string id, string root) =>
        new(id, id, "file.local", [new("rootPath", root)], [], [], 0);

    private static FlowDefinitionDto Csv2Json(string source, string target) => new(
        new SourceSpecDto(source, "*.csv", [new("format", "csv")]),
        new TargetSpecDto(target, "{runId}.json", "createNew", [new("format", "json")]),
        new MappingSpecDto("identity", []), 100, 1000);

    private static async Task<JsonNode> WaitForEnd(RpcTestClient c, string runId)
    {
        for (var i = 0; i < 200; i++)
        {
            var run = (await c.CallAsync("runs.get", new IdParams(runId))).Result()["run"]!;
            if (run["endedAt"] is not null)
            {
                return run;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException();
    }

    [Fact]
    public async Task Connection_flow_and_run_end_to_end()
    {
        var input = Folder("in");
        var output = Folder("out");
        await File.WriteAllTextAsync(Path.Combine(input, "a.csv"), "id;nom\n1;A\n2;B\n");
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        (await c.CallAsync("events.subscribe", new EventsSubscribeParams(["runs.changed"]))).Result();

        var connectors = (await c.CallAsync("connectors.list")).Result();
        connectors["connectors"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ShouldBe(["file.local", "file.sftp", "file.ftp", "rest", "db.sqlite", "db.postgresql", "db.sqlserver", "db.mysql", "db.oracle", "db.odbc"]);
        connectors["formats"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ShouldBe(["csv", "json", "xml", "xlsx"]);

        (await c.CallAsync("connections.save", Local("src", input))).Result();
        (await c.CallAsync("connections.save", Local("dst", output))).Result();
        (await c.CallAsync("connections.test", new IdParams("src"))).Result()["success"]!.GetValue<bool>().ShouldBeTrue();

        var flow = (await c.CallAsync("flows.save", new FlowSaveParams("demo", "Démo", "CSV vers JSON", Csv2Json("src", "dst"), 0))).Result();
        flow["draftVersion"]!.GetValue<long>().ShouldBe(1);
        (await c.CallAsync("runs.start", new RunStartParams("demo", false))).ErrorCode().ShouldBe(RpcErrorCodes.Conflict);
        (await c.CallAsync("flows.publish", new FlowPublishParams("demo", 1))).Result()["publishedVersion"]!.GetValue<long>().ShouldBe(1);

        var run = (await c.CallAsync("runs.start", new RunStartParams("demo", false))).Result();
        var done = await WaitForEnd(c, run["id"]!.GetValue<string>());
        done["status"]!.GetValue<string>().ShouldBe("succeeded");
        done["statusLabel"]!.GetValue<string>().ShouldBe("Réussi");
        done["writtenCount"]!.GetValue<long>().ShouldBe(2);
        File.Exists(Path.Combine(output, run["id"]!.GetValue<string>() + ".json")).ShouldBeTrue();

        var list = (await c.CallAsync("runs.list", new RunsListParams("demo", null, 1, 20))).Result();
        list["total"]!.GetValue<int>().ShouldBe(1);
        var detail = (await c.CallAsync("runs.get", new IdParams(run["id"]!.GetValue<string>()))).Result();
        detail["logs"]!.AsArray().Count.ShouldBeGreaterThanOrEqualTo(2);

        var statuses = c.Notifications.Where(n => n["method"]?.GetValue<string>() == "runs.changed").Select(n => n["params"]!["status"]!.GetValue<string>()).ToList();
        for (var i = 0; i < 10 && !statuses.Contains("succeeded"); i++)
        {
            var n = await c.ReadNotificationAsync(2000);
            if (n?["method"]?.GetValue<string>() == "runs.changed")
            {
                statuses.Add(n["params"]!["status"]!.GetValue<string>());
            }
        }

        statuses.ShouldContain("succeeded");
        var got = (await c.CallAsync("flows.get", new FlowGetParams("demo", null))).Result();
        got["definition"]!["source"]!["connectionId"]!.GetValue<string>().ShouldBe("src");
    }

    [Fact]
    public async Task Secrets_are_never_returned_by_any_method()
    {
        var generated = "v-" + Guid.NewGuid().ToString("N"); // générée à l'exécution
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        var saved = await c.CallAsync("connections.save", new ConnectionSaveParams("api", "API", "rest", [new("baseUrl", "https://api.example/")], [new("token", generated)], [], 0));
        saved.ToJsonString().ShouldNotContain(generated);
        saved.Result()["secretFields"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldBe(["token"]);
        (await c.CallAsync("connections.list")).ToJsonString().ShouldNotContain(generated);
        (await c.CallAsync("audit.query", new AuditQueryParams(1, 50, null, null, null))).ToJsonString().ShouldNotContain(generated);
    }

    [Fact]
    public async Task Roles_restrict_flow_operations()
    {
        var reader = Identities.ForUser("5001");
        var op = Identities.ForUser("5002");
        await using var admin = await _engine.ConnectAsync(Identities.Admin);
        (await admin.CallAsync("users.assignRole", new AssignRoleParams(reader.UserSid, reader.UserName, "user", "reader"))).Result();
        (await admin.CallAsync("users.assignRole", new AssignRoleParams(op.UserSid, op.UserName, "user", "operator"))).Result();
        (await admin.CallAsync("connections.save", Local("src", Folder("in")))).Result();
        (await admin.CallAsync("connections.save", Local("dst", Folder("out")))).Result();
        (await admin.CallAsync("flows.save", new FlowSaveParams("f", "F", "", Csv2Json("src", "dst"), 0))).Result();

        await using var r = await _engine.ConnectAsync(reader);
        (await r.CallAsync("flows.list")).Result();
        (await r.CallAsync("connections.save", Local("x", Folder("x")))).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
        (await r.CallAsync("runs.start", new RunStartParams("f", false))).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
        (await r.CallAsync("connections.test", new IdParams("src"))).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);

        await using var o = await _engine.ConnectAsync(op);
        (await o.CallAsync("runs.start", new RunStartParams("f", true))).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
        (await o.CallAsync("flows.save", new FlowSaveParams("g", "G", "", Csv2Json("src", "dst"), 0))).ErrorCode().ShouldBe(RpcErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Invalid_inputs_return_field_errors()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        var bad = await c.CallAsync("connections.save", new ConnectionSaveParams("BAD ID", "", "inconnu", [], [], [], 0));
        bad.ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        var badFlow = await c.CallAsync("flows.save", new FlowSaveParams(null, "x", "", Csv2Json("absent", "absent"), 0));
        badFlow.ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        (await c.CallAsync("runs.list", new RunsListParams(null, "inconnu", 1, 10))).ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        (await c.CallAsync("runs.get", new IdParams("absent"))).ErrorCode().ShouldBe(RpcErrorCodes.NotFound);
    }

    [Fact]
    public async Task Design_methods_preview_discover_and_list_functions()
    {
        var input = Folder("in");
        await File.WriteAllTextAsync(Path.Combine(input, "a.csv"), "id;nom\n1;A\n2;B\n3;C\n");
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        (await c.CallAsync("connections.save", Local("src", input))).Result();
        (await c.CallAsync("connections.save", Local("dst", Folder("out")))).Result();

        var discovered = (await c.CallAsync("connections.discover", new ConnectionDiscoverParams("src", "*.csv"))).Result();
        discovered["resources"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldBe(["a.csv"]);

        var functions = (await c.CallAsync("flows.functions")).Result()["functions"]!.AsArray();
        functions.Select(f => f!["name"]!.GetValue<string>()).ShouldContain("padLeft");

        var def = Csv2Json("src", "dst") with
        {
            Mapping = new MappingSpecDto("explicit", [new("label", "lower([nom])")]),
            Steps = [new StepSpecDto("filter", null, "[id] <> '2'", null, null)],
        };
        var preview = (await c.CallAsync("flows.preview", new FlowPreviewParams(def, 10))).Result();
        preview["rows"]!.AsArray().Select(r => r!["fate"]!.GetValue<string>()).ShouldBe(["output", "filtered", "output"]);
        preview["rows"]![2]!["outputJson"]!.GetValue<string>().ShouldBe("{\"label\":\"c\"}");
        Directory.GetFiles(Path.Combine(_dir.Path, "out")).ShouldBeEmpty();

        var bad = def with { Steps = [new StepSpecDto("filter", null, "upper(", null, null)] };
        (await c.CallAsync("flows.preview", new FlowPreviewParams(bad, 10))).ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Steps_survive_a_save_and_get_round_trip()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        (await c.CallAsync("connections.save", Local("src", Folder("in")))).Result();
        (await c.CallAsync("connections.save", Local("dst", Folder("out")))).Result();
        var def = Csv2Json("src", "dst") with { Steps = [new StepSpecDto("validate", null, "len([id]) > 0", null, "id vide"), new StepSpecDto("rename", "nom", null, "name", null)] };
        var saved = (await c.CallAsync("flows.save", new FlowSaveParams(null, "étapes", "", def, 0))).Result();
        var got = (await c.CallAsync("flows.get", new FlowGetParams(saved["id"]!.GetValue<string>(), null))).Result();
        var steps = got["definition"]!["steps"]!.AsArray();
        steps.Count.ShouldBe(2);
        steps[0]!["message"]!.GetValue<string>().ShouldBe("id vide");
        steps[1]!["target"]!.GetValue<string>().ShouldBe("name");
    }
}
