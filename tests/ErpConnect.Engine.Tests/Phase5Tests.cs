using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ErpConnect.Connectors.Tests;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Runs;
using ErpConnect.Flows;
using ErpConnect.Storage.Repositories;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Engine.Tests;

public sealed class Phase5Tests : IAsyncLifetime
{
    private Kernel _k = null!;
    private FakeClock _clock = null!;
    private SchedulerService _scheduler = null!;

    public async Task InitializeAsync()
    {
        _k = await Kernel.StartAsync();
        _clock = new FakeClock { UtcNow = DateTimeOffset.UtcNow };
        _scheduler = NewScheduler();
    }

    private SchedulerService NewScheduler() => new(new ScheduleRepository(_k.ConfigDb), _k.Flows, _k.Runs, _k.Audit, _clock, _k.RunRepository);

    public async Task DisposeAsync()
    {
        await _scheduler.DisposeAsync();
        await _k.DisposeAsync();
    }

    private async Task<FlowRecord> PublishedCsvToJsonAsync(int rows = 3, string? dedupKey = null)
    {
        var input = _k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), rows);
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", _k.Folder("out"));
        var flow = await _k.Flows.SaveAsync(Kernel.Admin, null, "planifié", "", Kernel.Definition("src", "*.csv", "csv", "dst", "{runId}.json") with { DedupKey = dedupKey }, 0, "t", default);
        return await _k.Flows.PublishAsync(Kernel.Admin, flow.Id, 1, "t", default);
    }

    private async Task<RunRecord> EndOf(RunRecord run)
    {
        await _k.Runs.WaitAsync(run.Id).WaitAsync(TimeSpan.FromMinutes(2));
        return (await _k.Runs.GetAsync(run.Id, default))!;
    }

    // ------------------------------------------------------------------ planification

    [Theory]
    [InlineData("pas un cron", "Europe/Paris", "cron")]
    [InlineData("0 6 * * *", "Mars/Olympus", "timeZone")]
    [InlineData("61 * * * *", "Europe/Paris", "cron")]
    public async Task Invalid_schedules_are_refused(string cron, string zone, string field)
    {
        var flow = await PublishedCsvToJsonAsync();
        var ex = await Should.ThrowAsync<EngineException>(() => _scheduler.SaveAsync(Kernel.Admin, flow.Id, cron, zone, true, 0, 60, 0, "t", default));
        ex.Fields.ShouldContain(f => f.Field == field);
    }

    [Fact]
    public async Task An_unpublished_flow_cannot_be_scheduled()
    {
        await _k.LocalAsync("src", _k.Folder("in"));
        await _k.LocalAsync("dst", _k.Folder("out"));
        var flow = await _k.Flows.SaveAsync(Kernel.Admin, null, "brouillon", "", Kernel.Definition("src", "*.csv", "csv", "dst", "x.json"), 0, "t", default);
        var ex = await Should.ThrowAsync<EngineException>(() => _scheduler.SaveAsync(Kernel.Admin, flow.Id, "0 6 * * *", "Europe/Paris", true, 0, 60, 0, "t", default));
        ex.Fields.ShouldContain(f => f.Field == "enabled");
    }

    [Fact]
    public void Next_occurrence_follows_the_time_zone_and_daylight_saving()
    {
        SchedulerService.Next("0 6 * * *", "Europe/Paris", new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero), "heure d'hiver : UTC+1");
        SchedulerService.Next("0 6 * * *", "Europe/Paris", new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 3, 29, 4, 0, 0, TimeSpan.Zero), "passage à l'heure d'été : UTC+2");
        SchedulerService.Next("*/15 8-18 * * 1-5", "UTC", new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero), "samedi → lundi 08:00");
    }

    [Fact]
    public async Task Due_schedule_starts_the_published_version_once_and_missed_occurrences_collapse()
    {
        var flow = await PublishedCsvToJsonAsync();
        var s = await _scheduler.SaveAsync(Kernel.Admin, flow.Id, "0 * * * *", "UTC", true, 0, 60, 0, "t", default);
        (await _scheduler.TickAsync(default)).ShouldBeEmpty("pas encore l'heure");

        _clock.Advance(TimeSpan.FromDays(3)); // 72 occurrences manquées (service arrêté)
        var started = await _scheduler.TickAsync(default);
        started.Count.ShouldBe(1);
        var run = await EndOf(started[0]);
        run.ShouldHaveStatus(RunStatus.Succeeded);
        (run.Trigger, run.FlowVersion, run.TriggeredBy).ShouldBe(("schedule", 1L, "Planificateur"));
        (await _scheduler.TickAsync(default)).ShouldBeEmpty("prochaine occurrence dans l'heure suivante");

        var after = (await _scheduler.ListAsync(default)).Single();
        after.LastRunId.ShouldBe(run.Id);
        after.NextRunAt!.Value.ShouldBeGreaterThan(_clock.UtcNow);
        after.Version.ShouldBe(s.Version, "l'état du planificateur ne change pas la version de configuration");
    }

    [Fact]
    public async Task A_disabled_schedule_never_fires()
    {
        var flow = await PublishedCsvToJsonAsync();
        await _scheduler.SaveAsync(Kernel.Admin, flow.Id, "* * * * *", "UTC", false, 0, 60, 0, "t", default);
        _clock.Advance(TimeSpan.FromHours(2));
        (await _scheduler.TickAsync(default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Overlapping_occurrence_is_skipped_with_a_warning_alert()
    {
        var input = _k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 50);
        using var gate = new SemaphoreSlim(0);
        using var server = new TestHttpServer((_, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(20));
            return (200, "{}", null);
        });
        await _k.LocalAsync("src", input);
        await _k.RestAsync("api", server.BaseUrl);
        var flow = await _k.Flows.SaveAsync(Kernel.Admin, null, "lent", "", Kernel.Definition("src", "*.csv", "csv", "api", "/x", [new("mode", "batch")], batch: 10), 0, "t", default);
        await _k.Flows.PublishAsync(Kernel.Admin, flow.Id, 1, "t", default);
        await _scheduler.SaveAsync(Kernel.Admin, flow.Id, "* * * * *", "UTC", true, 0, 60, 0, "t", default);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var first = (await _scheduler.TickAsync(default)).Single();
        _clock.Advance(TimeSpan.FromMinutes(1));
        (await _scheduler.TickAsync(default)).ShouldBeEmpty("l'exécution précédente tourne encore");
        gate.Release(100);
        (await EndOf(first)).ShouldHaveStatus(RunStatus.Succeeded);
        (await _k.RunRepository.ListAsync(true, 10, default)).ShouldContain(a => a.Severity == "warning" && a.Message.Contains("sautée", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Transient_failure_is_retried_with_exponential_delay_from_the_checkpoint_then_alerts_once()
    {
        var input = _k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 30);
        var down = true;
        var received = new ConcurrentBag<string>();
        using var server = new TestHttpServer((_, body) =>
        {
            if (Volatile.Read(ref down) && body.Contains("\"id\":\"15\"", StringComparison.Ordinal))
            {
                return (503, "indisponible", null);
            }

            received.Add(JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!);
            return (201, "{}", null);
        });
        await _k.LocalAsync("src", input);
        await _k.RestAsync("api", server.BaseUrl);
        var flow = await _k.Flows.SaveAsync(Kernel.Admin, null, "fragile", "", Kernel.Definition("src", "*.csv", "csv", "api", "/x", [], batch: 10), 0, "t", default);
        await _k.Flows.PublishAsync(Kernel.Admin, flow.Id, 1, "t", default);
        await _scheduler.SaveAsync(Kernel.Admin, flow.Id, "0 0 1 1 *", "UTC", true, 2, 60, 0, "t", default);
        _clock.UtcNow = new DateTimeOffset(_clock.UtcNow.Year + 1, 1, 1, 0, 1, 0, TimeSpan.Zero);

        var first = await EndOf((await _scheduler.TickAsync(default)).Single());
        first.ShouldHaveStatus(RunStatus.Failed);
        first.ErrorKind.ShouldBe("transient");
        first.Checkpoint.ShouldNotBeNull("les lots déjà confirmés ne seront pas renvoyés");

        (await _scheduler.TickAsync(default)).ShouldBeEmpty("calcule l'heure de la 1re nouvelle tentative");
        var state = (await _scheduler.ListAsync(default)).Single();
        (state.RetryAttempt, state.RetryAt).ShouldBe((1, _clock.UtcNow.AddSeconds(60)));
        _clock.UtcNow = state.RetryAt!.Value.AddSeconds(1);
        var second = await EndOf((await _scheduler.TickAsync(default)).Single());
        (second.Trigger, second.ParentRunId, second.Status).ShouldBe(("retry", first.Id, RunStatus.Failed));

        (await _scheduler.TickAsync(default)).ShouldBeEmpty();
        state = (await _scheduler.ListAsync(default)).Single();
        state.RetryAt.ShouldBe(_clock.UtcNow.AddSeconds(120), "délai doublé");
        Volatile.Write(ref down, false);
        _clock.UtcNow = state.RetryAt!.Value.AddSeconds(1);
        var third = await EndOf((await _scheduler.TickAsync(default)).Single());
        third.ShouldHaveStatus(RunStatus.Succeeded);
        received.Distinct().Count().ShouldBe(30, "chaque enregistrement est arrivé");
        received.Count(id => id == "1").ShouldBe(1, "les lots confirmés avant la panne ne sont pas renvoyés");
    }

    [Fact]
    public async Task Exhausted_retries_raise_a_single_alert()
    {
        var input = _k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 5);
        using var server = new TestHttpServer((_, _) => (503, "toujours en panne", null));
        await _k.LocalAsync("src", input);
        await _k.RestAsync("api", server.BaseUrl);
        var flow = await _k.Flows.SaveAsync(Kernel.Admin, null, "panne", "", Kernel.Definition("src", "*.csv", "csv", "api", "/x", []), 0, "t", default);
        await _k.Flows.PublishAsync(Kernel.Admin, flow.Id, 1, "t", default);
        await _scheduler.SaveAsync(Kernel.Admin, flow.Id, "0 0 1 1 *", "UTC", true, 1, 10, 0, "t", default);
        _clock.UtcNow = new DateTimeOffset(_clock.UtcNow.Year + 1, 1, 1, 0, 1, 0, TimeSpan.Zero);
        await EndOf((await _scheduler.TickAsync(default)).Single());
        await _scheduler.TickAsync(default);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await EndOf((await _scheduler.TickAsync(default)).Single());
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(5));
            (await _scheduler.TickAsync(default)).ShouldBeEmpty();
        }

        (await _k.RunRepository.ListAsync(true, 50, default)).Count(a => a.Message.Contains("après 1 nouvelle", StringComparison.Ordinal)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ reprise

    [Fact]
    public async Task Suspended_run_resumes_in_place_without_losing_or_double_counting()
    {
        var input = _k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 600);
        var received = new ConcurrentBag<string>();
        using var server = new TestHttpServer((_, body) =>
        {
            Thread.Sleep(3);
            received.Add(JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!);
            return (201, "{}", null);
        });
        await _k.LocalAsync("src", input);
        await _k.RestAsync("api", server.BaseUrl);
        var flow = await _k.Flows.SaveAsync(Kernel.Admin, null, "long", "", Kernel.Definition("src", "*.csv", "csv", "api", "/x", [], batch: 50), 0, "t", default);
        var run = await _k.Runs.StartAsync(Kernel.Admin, flow.Id, true, "t", default);
        while (server.RequestCount < 120)
        {
            await Task.Delay(10);
        }

        await _k.RestartRunsAsync(); // arrêt du service en pleine exécution
        var suspended = (await _k.Runs.GetAsync(run.Id, default))!;
        suspended.ShouldHaveStatus(RunStatus.Suspended);
        suspended.WrittenCount.ShouldBeGreaterThanOrEqualTo(100);

        var resumed = await _k.Runs.ResumeAsync(Kernel.Admin, run.Id, "t", default);
        resumed.Id.ShouldBe(run.Id, "reprise sur place");
        var done = await EndOf(resumed);
        done.ShouldHaveStatus(RunStatus.Succeeded);
        (done.ReadCount, done.WrittenCount).ShouldBe((600L, 600L));
        received.Distinct().Count().ShouldBe(600);
        (received.Count - 600).ShouldBeLessThanOrEqualTo(50, "au plus le lot interrompu est renvoyé (au moins une fois)");
    }

    [Fact]
    public async Task Permanent_failures_and_finished_runs_cannot_be_resumed()
    {
        await _k.LocalAsync("src", _k.Folder("in"));
        await File.WriteAllTextAsync(Path.Combine(_k.Folder("bad"), "a.json"), "pas du json");
        await _k.LocalAsync("ko", Path.Combine(_k.Dir.Path, "bad"));
        var failed = await _k.FlowRunAsync(Kernel.Definition("ko", "*.json", "json", "src", "x.json"));
        failed.ShouldHaveStatus(RunStatus.Failed);
        failed.ErrorKind.ShouldBe("permanent");
        await Should.ThrowAsync<EngineException>(() => _k.Runs.ResumeAsync(Kernel.Admin, failed.Id, "t", default));
        var ok = await _k.FlowRunAsync(Kernel.Definition("src", "*.csv", "csv", "src", "y.json"));
        (await Should.ThrowAsync<EngineException>(() => _k.Runs.ResumeAsync(Kernel.Admin, ok.Id, "t", default))).Kind.ShouldBe(EngineErrorKind.Conflict);
    }

    // ------------------------------------------------------------------ anti-doublons

    [Fact]
    public async Task Dedup_key_skips_records_already_sent_by_this_flow_even_across_runs()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        await File.WriteAllTextAsync(Path.Combine(input, "a.csv"), "id;nom\n1;A\n2;B\n3;C\n3;C bis\n");
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        var def = Kernel.Definition("src", "*.csv", "csv", "dst", "{runId}.json") with { DedupKey = "trim([id])" };
        var flow = await _k.Flows.SaveAsync(Kernel.Admin, null, "dédoublonné", "", def, 0, "t", default);
        var first = await _k.RunToEndAsync(flow.Id);
        (first.ReadCount, first.WrittenCount, first.SkippedCount).ShouldBe((4L, 3L, 1L));

        await File.WriteAllTextAsync(Path.Combine(input, "b.csv"), "id;nom\n3;C\n4;D\n");
        var second = await _k.RunToEndAsync(flow.Id);
        (second.ReadCount, second.WrittenCount, second.SkippedCount).ShouldBe((6L, 1L, 5L));
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, second.Id + ".json"))).RootElement;
        json.GetArrayLength().ShouldBe(1);
        json[0].GetProperty("id").GetString().ShouldBe("4");

        var other = await _k.Flows.SaveAsync(Kernel.Admin, null, "autre flux", "", def, 0, "t", default);
        (await _k.RunToEndAsync(other.Id)).WrittenCount.ShouldBe(4, "registre propre à chaque flux : 1, 2, 3, 4 (doublon de 3 ignoré)");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Encoding.UTF8.GetString(await SharedFile.ReadAllBytesAsync(_k.RunsDb.Path)).ShouldNotContain("C bis");
    }

    [Fact]
    public async Task Keys_of_an_unpublished_file_are_not_registered()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        await File.WriteAllTextAsync(Path.Combine(input, "a.csv"), "id;nom\n1;A\n2;\n");
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        var def = Kernel.Definition("src", "*.csv", "csv", "dst", "{runId}.json") with
        {
            DedupKey = "[id]",
            Steps = [new StepSpec("validate", Expression: "not isEmpty([nom])", Message: "nom vide")],
        };
        var flow = await _k.Flows.SaveAsync(Kernel.Admin, null, "tout ou rien", "", def, 0, "t", default);
        (await _k.RunToEndAsync(flow.Id)).ShouldHaveStatus(RunStatus.Failed);
        await File.WriteAllTextAsync(Path.Combine(input, "a.csv"), "id;nom\n1;A\n2;B\n");
        var again = await _k.RunToEndAsync(flow.Id);
        again.ShouldHaveStatus(RunStatus.Succeeded);
        again.WrittenCount.ShouldBe(2, "l'échec n'a rien publié, donc rien n'a été enregistré comme transmis");
    }

    [Fact]
    public async Task Empty_dedup_key_is_a_reject()
    {
        var input = _k.Folder("in");
        await File.WriteAllTextAsync(Path.Combine(input, "a.csv"), "id;nom\n;A\n");
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", _k.Folder("out"));
        var run = await _k.FlowRunAsync(Kernel.Definition("src", "*.csv", "csv", "dst", "x.json") with { DedupKey = "[id]" });
        run.ShouldHaveStatus(RunStatus.Failed);
        (await _k.Runs.RejectsAsync(run.Id, 5, default)).Single().Reason.ShouldContain("Clé anti-doublon");
    }

    // ------------------------------------------------------------------ alertes

    [Fact]
    public async Task Failed_runs_raise_an_error_alert_that_can_be_acknowledged_once()
    {
        await _k.LocalAsync("src", _k.Folder("in"));
        await File.WriteAllTextAsync(Path.Combine(_k.Folder("bad"), "a.json"), "pas du json");
        await _k.LocalAsync("ko", Path.Combine(_k.Dir.Path, "bad"));
        var failed = await _k.FlowRunAsync(Kernel.Definition("ko", "*.json", "json", "src", "x.json"));
        var alert = (await _k.RunRepository.ListAsync(true, 10, default)).Single(a => a.RunId == failed.Id);
        alert.Severity.ShouldBe("error");
        (await _k.RunRepository.AcknowledgeAsync(alert.Id, "PC\\alice", DateTimeOffset.UtcNow, default)).ShouldBeTrue();
        (await _k.RunRepository.AcknowledgeAsync(alert.Id, "PC\\bob", DateTimeOffset.UtcNow, default)).ShouldBeFalse();
        (await _k.RunRepository.ListAsync(true, 10, default)).ShouldNotContain(a => a.Id == alert.Id);
        (await _k.RunRepository.ListAsync(false, 10, default)).Single(a => a.Id == alert.Id).AcknowledgedBy.ShouldBe("PC\\alice");
    }

    // ------------------------------------------------------------------ base de données

    [Fact]
    public async Task Csv_to_sqlite_then_sqlite_to_json_end_to_end()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        var db = Path.Combine(_k.Folder("db"), "erp.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE client (code TEXT PRIMARY KEY, nom TEXT NOT NULL, montant REAL);";
            cmd.ExecuteNonQuery();
        }

        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 120);
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        await _k.Connections.SaveAsync(Kernel.Admin, "base", "base", "db.sqlite", new Dictionary<string, string?> { ["path"] = db }, new Dictionary<string, string?>(), [], 0, "t", default);
        var load = await _k.FlowRunAsync(new FlowDefinition(
            new SourceSpec("src", "*.csv", [new("format", "csv")]),
            new TargetSpec("base", "client", "createNew", []),
            new MappingSpec("explicit", [new("code", "padLeft([id], 5, '0')"), new("nom", "[nom]"), new("montant", "toNumber([montant])")]), 50));
        load.ShouldHaveStatus(RunStatus.Succeeded);
        load.WrittenCount.ShouldBe(120);

        var again = await _k.FlowRunAsync(new FlowDefinition(
            new SourceSpec("src", "*.csv", [new("format", "csv")]),
            new TargetSpec("base", "client", "createNew", []),
            new MappingSpec("explicit", [new("code", "padLeft([id], 5, '0')"), new("nom", "[nom]")]), 50));
        again.ShouldHaveStatus(RunStatus.Failed);
        again.RejectedCount.ShouldBe(120, "clé primaire déjà présente : chaque ligne est un rejet motivé");
        (await _k.Runs.RejectsAsync(again.Id, 1, default)).Single().Stage.ShouldBe("write");

        var export = await _k.FlowRunAsync(new FlowDefinition(
            new SourceSpec("base", "SELECT code, montant FROM client WHERE montant >= 100 ORDER BY code", []),
            new TargetSpec("dst", "export.json", "createNew", [new("format", "json")]),
            new MappingSpec("identity", []), 7));
        export.ShouldHaveStatus(RunStatus.Succeeded);
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "export.json"))).RootElement;
        json.GetArrayLength().ShouldBe(20);
        json[0].GetProperty("code").GetString().ShouldBe("00100");
        json[0].GetProperty("montant").GetDouble().ShouldBe(100.5);
    }
}
