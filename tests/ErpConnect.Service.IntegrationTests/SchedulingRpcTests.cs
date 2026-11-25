using System.Text.Json.Nodes;
using ErpConnect.Contracts;
using ErpConnect.Tests.Engine;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Service.IntegrationTests;

/// <summary>Phase 5 par le pipe : planifications, reprise, alertes et leur notification.</summary>
public sealed class SchedulingRpcTests : IAsyncLifetime
{
    private readonly TempDirectory _dir = new();
    private EngineHarness _engine = null!;

    public async Task InitializeAsync() => _engine = await EngineHarness.StartAsync(_dir.Path, new FakeClock { UtcNow = DateTimeOffset.UtcNow });

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

    private async Task<string> PublishedFlowAsync(RpcTestClient c, string sourceRoot)
    {
        (await c.CallAsync("connections.save", new ConnectionSaveParams("src", "src", "file.local", [new("rootPath", sourceRoot)], [], [], 0))).Result();
        (await c.CallAsync("connections.save", new ConnectionSaveParams("dst", "dst", "file.local", [new("rootPath", Folder("out"))], [], [], 0))).Result();
        var def = new FlowDefinitionDto(new SourceSpecDto("src", "*.json", [new("format", "json")]), new TargetSpecDto("dst", "{runId}.json", "createNew", [new("format", "json")]),
            new MappingSpecDto("identity", []), 100, 10);
        var flow = (await c.CallAsync("flows.save", new FlowSaveParams(null, "planifié", "", def, 0))).Result();
        var id = flow["id"]!.GetValue<string>();
        (await c.CallAsync("flows.publish", new FlowPublishParams(id, 1))).Result();
        return id;
    }

    [Fact]
    public async Task Schedule_lifecycle_with_optimistic_concurrency()
    {
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        var flowId = await PublishedFlowAsync(c, Folder("in"));
        var bad = await c.CallAsync("schedules.save", new ScheduleSaveParams(flowId, "chaque matin", "Europe/Paris", true, 0, 60, 0));
        bad.ErrorCode().ShouldBe(RpcErrorCodes.ValidationFailed);
        bad["error"]!["data"]!.ToJsonString().ShouldContain("cron");

        var s = (await c.CallAsync("schedules.save", new ScheduleSaveParams(flowId, "0 6 * * 1-5", "Europe/Paris", true, 3, 300, 0))).Result();
        s["version"]!.GetValue<long>().ShouldBe(1);
        s["nextRunAt"].ShouldNotBeNull();
        (await c.CallAsync("schedules.save", new ScheduleSaveParams(flowId, "0 7 * * *", "Europe/Paris", true, 3, 300, 0))).ErrorCode().ShouldBe(RpcErrorCodes.Conflict);
        (await c.CallAsync("schedules.save", new ScheduleSaveParams(flowId, "0 7 * * *", "Europe/Paris", false, 3, 300, 1))).Result()["enabled"]!.GetValue<bool>().ShouldBeFalse();
        (await c.CallAsync("schedules.list")).Result()["schedules"]!.AsArray().Single()!["cron"]!.GetValue<string>().ShouldBe("0 7 * * *");
        (await c.CallAsync("schedules.delete", new IdParams(flowId))).Result()["deleted"]!.GetValue<bool>().ShouldBeTrue();
        (await c.CallAsync("schedules.delete", new IdParams(flowId))).ErrorCode().ShouldBe(RpcErrorCodes.NotFound);
        var audit = (await c.CallAsync("audit.query", new AuditQueryParams(1, 50, null, null, null))).Result()["items"]!.AsArray();
        audit.Count(e => e!["action"]!.GetValue<string>() == "schedules.save").ShouldBe(2);
    }

    [Fact]
    public async Task Failure_raises_an_alert_pushed_to_subscribers_and_acknowledged_by_rpc()
    {
        var input = Folder("in");
        await File.WriteAllTextAsync(Path.Combine(input, "a.json"), "pas du json");
        await using var watcher = await _engine.ConnectAsync(Identities.Admin);
        (await watcher.CallAsync("events.subscribe", new EventsSubscribeParams(["alerts.raised"]))).Result();
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        var flowId = await PublishedFlowAsync(c, input);
        var run = (await c.CallAsync("runs.start", new RunStartParams(flowId, false))).Result();
        var n = await watcher.ReadNotificationAsync(5000);
        n.ShouldNotBeNull();
        n["method"]!.GetValue<string>().ShouldBe("alerts.raised");
        n["params"]!["severity"]!.GetValue<string>().ShouldBe("error");

        var alerts = (await c.CallAsync("alerts.list", new AlertsListParams(true, 10))).Result()["alerts"]!.AsArray();
        var alert = alerts.Single()!;
        alert["runId"]!.GetValue<string>().ShouldBe(run["id"]!.GetValue<string>());
        (await c.CallAsync("alerts.acknowledge", new AlertAcknowledgeParams(alert["id"]!.GetValue<long>()))).Result()["acknowledged"]!.GetValue<bool>().ShouldBeTrue();
        (await c.CallAsync("alerts.list", new AlertsListParams(true, 10))).Result()["alerts"]!.AsArray().ShouldBeEmpty();

        var detail = (await c.CallAsync("runs.get", new IdParams(run["id"]!.GetValue<string>()))).Result()["run"]!;
        detail["errorKind"]!.GetValue<string>().ShouldBe("permanent");
        (await c.CallAsync("runs.resume", new IdParams(run["id"]!.GetValue<string>()))).ErrorCode().ShouldBe(RpcErrorCodes.Conflict);
    }

    [Fact]
    public async Task Scheduler_tick_runs_the_flow_through_the_real_runtime()
    {
        var input = Folder("in");
        await File.WriteAllTextAsync(Path.Combine(input, "a.json"), "[{\"id\":1}]");
        await using var c = await _engine.ConnectAsync(Identities.Admin);
        var flowId = await PublishedFlowAsync(c, input);
        (await c.CallAsync("schedules.save", new ScheduleSaveParams(flowId, "*/5 * * * *", "UTC", true, 0, 60, 0))).Result();
        _engine.Clock.Advance(TimeSpan.FromMinutes(6));
        var started = await _engine.Runtime.Scheduler.TickAsync(default);
        started.Count.ShouldBe(1);
        await _engine.Runtime.Runs.WaitAsync(started[0].Id);
        var runs = (await c.CallAsync("runs.list", new RunsListParams(flowId, null, 1, 10))).Result()["items"]!.AsArray();
        runs.Single()!["trigger"]!.GetValue<string>().ShouldBe("schedule");
        runs.Single()!["status"]!.GetValue<string>().ShouldBe("succeeded");
    }
}
