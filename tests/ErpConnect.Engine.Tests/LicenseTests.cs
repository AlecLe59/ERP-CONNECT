using System.Security.Cryptography;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Licensing;
using ErpConnect.Core.Runs;
using ErpConnect.Security.Licensing;
using ErpConnect.Storage.Repositories;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Engine.Tests;

/// <summary>Phase 7 : licence hors ligne, période d'évaluation, droits d'usage appliqués par le noyau et le planificateur.</summary>
public sealed class LicenseTests : IAsyncLifetime, IDisposable
{
    private const string Installation = "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed";
    private readonly ECDsa _vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeClock _clock = new() { UtcNow = DateTimeOffset.UtcNow };
    private Kernel _k = null!;
    private LicenseService _license = null!;
    private FlowService _flows = null!;
    private RunService _runs = null!;

    public async Task InitializeAsync()
    {
        _k = await Kernel.StartAsync();
        _license = new LicenseService(new LicenseRepository(_k.ConfigDb), new LicenseVerifier([_vendor.ExportSubjectPublicKeyInfo()]), new FlowRepository(_k.ConfigDb),
            _k.Audit, _clock, Installation, _clock.UtcNow);
        _flows = new FlowService(new FlowRepository(_k.ConfigDb), new ConnectionRepository(_k.ConfigDb), _k.Audit, _clock) { Entitlements = _license };
        _runs = new RunService(_k.RunRepository, _flows, _k.Connections, _k.Audit, _clock, null, Kernel.FastRetry) { Entitlements = _license };
    }

    public async Task DisposeAsync()
    {
        await _runs.DisposeAsync();
        await _k.DisposeAsync();
    }

    public void Dispose() => _vendor.Dispose();

    private string Issue(string installation = Installation, DateTimeOffset? expires = null, string[]? features = null, int? maxFlows = null, ECDsa? key = null) =>
        LicenseVerifier.Sign(new LicensePayload(1, "L-" + Guid.NewGuid().ToString("N")[..6], "Client SA", "Standard", installation, _clock.UtcNow,
            expires ?? _clock.UtcNow.AddYears(1), features ?? ["scheduler"], maxFlows), key ?? _vendor);

    private async Task<string> FlowAsync(string name = "f")
    {
        if (await _k.Connections.GetAsync("src", default) is null)
        {
            var input = _k.Folder("in");
            await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 2);
            await _k.LocalAsync("src", input);
            await _k.LocalAsync("dst", _k.Folder("out"));
        }

        return (await _flows.SaveAsync(Kernel.Admin, null, name, "", Kernel.Definition("src", "*.csv", "csv", "dst", "{runId}.json"), 0, "t", default)).Id;
    }

    [Fact]
    public async Task Evaluation_lasts_thirty_days_then_runs_and_publishing_stop()
    {
        var id = await FlowAsync();
        (await _license.GetStatusAsync(default)).State.ShouldBe("evaluation");
        await _flows.PublishAsync(Kernel.Admin, id, 1, "t", default);
        var run = await _runs.StartAsync(Kernel.Admin, id, false, "t", default);
        await _runs.WaitAsync(run.Id);

        _clock.Advance(LicenseService.EvaluationPeriod + TimeSpan.FromMinutes(1));
        (await _license.GetStatusAsync(default)).State.ShouldBe("evaluationExpired");
        var ex = await Should.ThrowAsync<EngineException>(() => _runs.StartAsync(Kernel.Admin, id, false, "t", default));
        (ex.Kind, ex.Message.Contains("évaluation", StringComparison.Ordinal)).ShouldBe((EngineErrorKind.Entitlement, true));
        var other = await FlowAsync("g");
        (await Should.ThrowAsync<EngineException>(() => _flows.PublishAsync(Kernel.Admin, other, 1, "t", default))).Kind.ShouldBe(EngineErrorKind.Entitlement);

        // Les données restent lisibles : historique et définitions.
        (await _k.Runs.GetAsync(run.Id, default))!.Status.ShouldBe(RunStatus.Succeeded);
        (await _flows.ListAsync(default)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Importing_a_valid_licence_restores_service_and_is_audited()
    {
        var id = await FlowAsync();
        _clock.Advance(TimeSpan.FromDays(40));
        var status = await _license.ImportAsync(Kernel.Admin, Issue(), "t", default);
        (status.State, status.License!.Customer).ShouldBe(("licensed", "Client SA"));
        await _flows.PublishAsync(Kernel.Admin, id, 1, "t", default);
        var run = await _runs.StartAsync(Kernel.Admin, id, false, "t", default);
        await _runs.WaitAsync(run.Id);
        var audit = await _k.Audit.QueryAsync(new Core.Audit.AuditQuery(1, 50, "license.import", null, null), default);
        audit.Items.ShouldContain(e => e.Outcome == "success");
    }

    [Fact]
    public async Task Licence_for_another_installation_or_forged_is_refused_and_audited()
    {
        var other = await Should.ThrowAsync<EngineException>(() => _license.ImportAsync(Kernel.Admin, Issue(installation: "autre-machine"), "t", default));
        other.Fields.ShouldContain(f => f.Field == "content" && f.Message.Contains("autre installation", StringComparison.Ordinal));
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (await Should.ThrowAsync<EngineException>(() => _license.ImportAsync(Kernel.Admin, Issue(key: attacker), "t", default))).Message.ShouldContain("Signature");
        (await _license.GetStatusAsync(default)).State.ShouldBe("evaluation");
        var audit = await _k.Audit.QueryAsync(new Core.Audit.AuditQuery(1, 50, "license.import", null, null), default);
        audit.Items.Count(e => e.Outcome == "failure").ShouldBe(2);
    }

    [Fact]
    public async Task Expired_licence_blocks_runs()
    {
        var id = await FlowAsync();
        await _flows.PublishAsync(Kernel.Admin, id, 1, "t", default);
        await _license.ImportAsync(Kernel.Admin, Issue(expires: _clock.UtcNow.AddDays(10)), "t", default);
        _clock.Advance(TimeSpan.FromDays(11));
        (await _license.GetStatusAsync(default)).State.ShouldBe("expired");
        (await Should.ThrowAsync<EngineException>(() => _runs.StartAsync(Kernel.Admin, id, false, "t", default))).Message.ShouldContain("expirée");
    }

    [Fact]
    public async Task Features_and_flow_cap_are_enforced()
    {
        await _license.ImportAsync(Kernel.Admin, Issue(features: [], maxFlows: 1), "t", default);
        var a = await FlowAsync("a");
        await _flows.PublishAsync(Kernel.Admin, a, 1, "t", default);
        await _flows.PublishAsync(Kernel.Admin, a, 1, "t", default); // republier ne consomme pas de place
        var b = await FlowAsync("b");
        (await Should.ThrowAsync<EngineException>(() => _flows.PublishAsync(Kernel.Admin, b, 1, "t", default))).Message.ShouldContain("1 flux publiés");

        (await _license.CheckAsync(Features.Scripts, default)).Allowed.ShouldBeFalse();
        (await Should.ThrowAsync<EngineException>(() => _runs.StartAsync(Kernel.Admin, a, false, "t", default, "schedule"))).Message.ShouldContain("scheduler");
        var manual = await _runs.StartAsync(Kernel.Admin, a, false, "t", default);
        await _runs.WaitAsync(manual.Id);
    }

    [Fact]
    public async Task Scheduler_without_the_feature_raises_an_alert_instead_of_running()
    {
        await _license.ImportAsync(Kernel.Admin, Issue(features: []), "t", default);
        var id = await FlowAsync();
        await _flows.PublishAsync(Kernel.Admin, id, 1, "t", default);
        await using var scheduler = new SchedulerService(new ScheduleRepository(_k.ConfigDb), _flows, _runs, _k.Audit, _clock, _k.RunRepository);
        await scheduler.SaveAsync(Kernel.Admin, id, "* * * * *", "UTC", true, 0, 60, 0, "t", default);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await scheduler.TickAsync(default);
        (await _k.RunRepository.QueryAsync(new RunQuery(id, null, 1, 10), default)).Items.ShouldBeEmpty();
        (await _k.RunRepository.ListAsync(true, 10, default)).ShouldContain(al => al.Message.Contains("lancement planifié impossible", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_the_licence_returns_to_evaluation_state()
    {
        await _license.ImportAsync(Kernel.Admin, Issue(), "t", default);
        (await _license.RemoveAsync(Kernel.Admin, "t", default)).ShouldBeTrue();
        (await _license.GetStatusAsync(default)).State.ShouldBe("evaluation");
    }
}
