using System.Text.Json;
using System.Text.Json.Nodes;
using ErpConnect.Contracts;
using ErpConnect.Tests.Engine;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Service.IntegrationTests;

/// <summary>Phase 6 par le pipe : essai de script, erreurs de champ, script bloqué, limites publiées.</summary>
public sealed class ScriptRpcTests : IAsyncLifetime
{
    private readonly TempDirectory _dir = new();
    private EngineHarness _engine = null!;

    public async Task InitializeAsync() => _engine = await EngineHarness.StartAsync(_dir.Path, new FakeClock { UtcNow = DateTimeOffset.UtcNow });

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _dir.Dispose();
    }

    private static async Task SetScriptsAsync(RpcTestClient c, bool enabled)
    {
        var current = (await c.CallAsync("settings.get")).Result();
        var settings = current["settings"]!.Deserialize<SettingsDto>(ErpConnect.Rpc.Dispatch.RpcDispatcher.SerializerOptions)!;
        (await c.CallAsync("settings.update", new SettingsUpdateParams(settings with { ScriptsEnabled = enabled }, current["version"]!.GetValue<long>()))).Result();
    }

    private static FlowDefinitionDto ScriptFlow(string code) => new(new SourceSpecDto("src", "*.json", [new("format", "json")]), new TargetSpecDto("dst", "o.json", "createNew", [new("format", "json")]),
        new MappingSpecDto("identity", []), 100, 10, [new StepSpecDto("script", null, code, null, null)]);

    [Fact]
    public async Task Scripts_are_disabled_by_default_and_the_switch_is_audited()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        (await c.CallAsync("settings.get")).Result()["settings"]!["scriptsEnabled"]!.GetValue<bool>().ShouldBeFalse();
        (await c.CallAsync("scripts.test", new ScriptTestParams("function transform(r) { return r; }", "{}"))).ErrorCode().ShouldBe(RpcErrorCodes.EntitlementDenied);
        var save = await c.CallAsync("flows.save", new FlowSaveParams(null, "script", "", ScriptFlow("function transform(r) { return r; }"), 0));
        save.ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        save["error"]!["data"]!["fields"]!.AsArray().ShouldContain(f => f!["field"]!.GetValue<string>() == "steps" && f["message"]!.GetValue<string>().Contains("désactivés", StringComparison.Ordinal));

        await SetScriptsAsync(c, true);
        var input = Path.Combine(_dir.Path, "in");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "a.json"), """[{"id":1}]""");
        (await c.CallAsync("connections.save", new ConnectionSaveParams("src", "src", "file.local", [new("rootPath", input)], [], [], 0))).Result();
        (await c.CallAsync("connections.save", new ConnectionSaveParams("dst", "dst", "file.local", [new("rootPath", _dir.Path)], [], [], 0))).Result();
        (await c.CallAsync("flows.save", new FlowSaveParams("avec-script", "script", "", ScriptFlow("function transform(r) { return r; }"), 0))).Result();
        var audit = (await c.CallAsync("audit.query", new AuditQueryParams(1, 50, null, null, null))).Result()["items"]!.AsArray();
        audit.ShouldContain(e => e!["action"]!.GetValue<string>() == "settings.update" && e["details"]!.ToJsonString().Contains("ScriptsEnabled", StringComparison.OrdinalIgnoreCase));

        // Désactiver ensuite bloque l'exécution d'un flux déjà enregistré avec un script.
        await SetScriptsAsync(c, false);
        var run = (await c.CallAsync("runs.start", new RunStartParams("avec-script", true))).Result();
        var id = run["id"]!.GetValue<string>();
        JsonNode? detail = null;
        for (var i = 0; i < 100; i++)
        {
            detail = (await c.CallAsync("runs.get", new IdParams(id))).Result()["run"];
            if (detail!["status"]!.GetValue<string>() is not ("queued" or "running"))
            {
                break;
            }

            await Task.Delay(50);
        }

        detail!["status"]!.GetValue<string>().ShouldBe("failed");
        detail["error"]!.GetValue<string>().ShouldContain("scripts sont désactivés");
    }

    [Fact]
    public async Task Script_test_returns_output_filtered_rejected_and_stopped()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        await SetScriptsAsync(c, true);
        var ok = (await c.CallAsync("scripts.test", new ScriptTestParams("function transform(r) { r.total = r.qte * r.pu; return r; }", """{"qte":3,"pu":2.5}"""))).Result();
        ok["fate"]!.GetValue<string>().ShouldBe("output");
        ok["outputJson"]!.GetValue<string>().ShouldContain("\"total\":7.5");

        (await c.CallAsync("scripts.test", new ScriptTestParams("function transform(r) { return null; }", "{}"))).Result()["fate"]!.GetValue<string>().ShouldBe("filtered");
        var rejected = (await c.CallAsync("scripts.test", new ScriptTestParams("function transform(r) { throw new Error('quantité négative'); }", "{}"))).Result();
        (rejected["fate"]!.GetValue<string>(), rejected["reason"]!.GetValue<string>()).ShouldBe(("rejected", "script : quantité négative"));
        var stopped = (await c.CallAsync("scripts.test", new ScriptTestParams("function transform(r) { while (true) {} }", "{}"))).Result();
        stopped["fate"]!.GetValue<string>().ShouldBe("stopped");
        stopped["reason"]!.GetValue<string>().ShouldStartWith("Script arrêté");

        // Le moteur reste disponible après un script bloqué.
        (await c.CallAsync("system.getStatus")).Result().ShouldNotBeNull();
    }

    [Fact]
    public async Task Invalid_code_or_record_are_field_errors()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        await SetScriptsAsync(c, true);
        var bad = await c.CallAsync("scripts.test", new ScriptTestParams("function transform(r) {", "{}"));
        bad.ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        bad["error"]!["data"]!.ToJsonString().ShouldContain("code");
        var badRecord = await c.CallAsync("scripts.test", new ScriptTestParams("function transform(r) { return r; }", "[1,2]"));
        badRecord.ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        badRecord["error"]!["data"]!.ToJsonString().ShouldContain("recordJson");
        var limits = (await c.CallAsync("scripts.limits")).Result();
        limits["timeoutMs"]!.GetValue<int>().ShouldBe(500);
        limits["maxSourceLength"]!.GetValue<int>().ShouldBe(65536);
    }

    [Fact]
    public async Task Flow_with_invalid_script_cannot_be_saved()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        await SetScriptsAsync(c, true);
        var r = await c.CallAsync("flows.save", new FlowSaveParams(null, "script", "", ScriptFlow("var x = 1;"), 0));
        r.ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        r["error"]!["data"]!.ToJsonString().ShouldContain("steps[0].expression");
    }
}
