using ErpConnect.Core.Flows;
using ErpConnect.Core.Runs;
using ErpConnect.Storage.Repositories;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Engine.Tests;

/// <summary>Phase 8 : purge selon les durées de rétention des paramètres.</summary>
public sealed class RetentionTests : IAsyncLifetime
{
    private readonly DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private Kernel _k = null!;

    public async Task InitializeAsync() => _k = await Kernel.StartAsync();

    public async Task DisposeAsync() => await _k.DisposeAsync();

    private async Task<string> RunAsync(int daysAgo, RunStatus end, string? parent = null)
    {
        var at = _now.AddDays(-daysAgo);
        var id = "r" + Guid.NewGuid().ToString("N")[..8];
        await _k.RunRepository.CreateAsync(new RunRecord(id, "f", 1, RunStatus.Pending, "t", parent, at, null, null, 0, 0, 0, 0, null, null, "manual"), default);
        await _k.RunRepository.TransitionAsync(id, RunStatus.Pending, RunStatus.Running, "t", null, at, default);
        if (end != RunStatus.Running)
        {
            await _k.RunRepository.TransitionAsync(id, RunStatus.Running, end, "t", null, at, default);
        }

        await _k.RunRepository.LogAsync(id, at, "info", "journal", default);
        await _k.RunRepository.SaveProgressAsync(id, new RunProgress(1, 0, 1, 0, null, [new RunRejectRecord(0, id, 1, 0, "mapping", "motif", "{}", at)], ["k-" + id]), default);
        return id;
    }

    [Fact]
    public async Task Each_retention_applies_to_its_own_data()
    {
        var recentOk = await RunAsync(5, RunStatus.Succeeded);
        var oldOk = await RunAsync(40, RunStatus.Succeeded);     // journaux > 30 j (réussite) : purgés ; rejets < 90 j : gardés
        var oldFailed = await RunAsync(100, RunStatus.Failed);   // journaux > 90 j (échec) et rejets > 90 j : purgés
        var ancient = await RunAsync(400, RunStatus.Succeeded);  // en-tête > 396 j : supprimé
        var running = await RunAsync(500, RunStatus.Running);    // jamais touchée : pas terminée
        var counts = await _k.RunRepository.PurgeAsync(new RetentionCutoffs(_now.AddDays(-396), _now.AddDays(-30), _now.AddDays(-90), _now.AddDays(-90), _now.AddDays(-396)), default);

        (await _k.RunRepository.LogsAsync(recentOk, 10, default)).ShouldNotBeEmpty();
        (await _k.RunRepository.LogsAsync(oldOk, 10, default)).ShouldBeEmpty();
        (await _k.RunRepository.RejectsAsync(oldOk, 10, default)).ShouldNotBeEmpty();
        (await _k.RunRepository.LogsAsync(oldFailed, 10, default)).ShouldBeEmpty();
        (await _k.RunRepository.RejectsAsync(oldFailed, 10, default)).ShouldBeEmpty();
        (await _k.RunRepository.GetAsync(oldFailed, default)).ShouldNotBeNull();
        (await _k.RunRepository.GetAsync(ancient, default)).ShouldBeNull();
        (await _k.RunRepository.GetAsync(running, default))!.Status.ShouldBe(RunStatus.Running);
        counts.Runs.ShouldBe(1);
        counts.DedupKeys.ShouldBe(0); // les clés sont datées du jour d'enregistrement (SaveProgress) : récentes
    }

    [Fact]
    public async Task A_parent_still_referenced_by_a_kept_run_is_kept()
    {
        var parent = await RunAsync(400, RunStatus.Failed);
        var child = await RunAsync(10, RunStatus.Succeeded, parent);
        await _k.RunRepository.PurgeAsync(new RetentionCutoffs(_now.AddDays(-396), _now.AddDays(-30), _now.AddDays(-90), _now.AddDays(-90), _now.AddDays(-396)), default);
        (await _k.RunRepository.GetAsync(parent, default)).ShouldNotBeNull();
        (await _k.RunRepository.GetAsync(child, default)).ShouldNotBeNull();

        // Quand l'enfant est lui-même ancien, les deux partent.
        var counts = await _k.RunRepository.PurgeAsync(new RetentionCutoffs(_now, _now, _now, _now, _now.AddDays(-396)), default);
        (await _k.RunRepository.GetAsync(parent, default)).ShouldBeNull();
        counts.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task Service_uses_settings_and_audits_non_empty_purges()
    {
        await RunAsync(400, RunStatus.Succeeded);
        var clock = new FakeClock { UtcNow = _now };
        var settings = new SettingsService(new SettingsRepository(_k.ConfigDb, clock), _k.Audit);
        var service = new RetentionService(settings, _k.RunRepository, _k.Audit, clock);
        (await service.PurgeAsync(default)).Runs.ShouldBe(1);
        (await service.PurgeAsync(default)).Total.ShouldBe(0);
        var audit = await _k.Audit.QueryAsync(new Core.Audit.AuditQuery(1, 10, "retention.purge", null, null), default);
        audit.Items.Count.ShouldBe(1);
        await service.DisposeAsync();
    }
}
