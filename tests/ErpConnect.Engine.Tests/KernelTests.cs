using System.Text;
using System.Text.Json;
using ErpConnect.Connectors.Tests;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Runs;
using ErpConnect.Flows;

namespace ErpConnect.Engine.Tests;

public sealed class KernelTests : IAsyncLifetime
{
    private Kernel _k = null!;

    public async Task InitializeAsync() => _k = await Kernel.StartAsync();

    public async Task DisposeAsync() => await _k.DisposeAsync();

    [Fact]
    public async Task Csv_to_json_succeeds_with_exact_counters_and_published_file()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        await Kernel.WriteCsvAsync(Path.Combine(input, "clients.csv"), 250);
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        var run = await _k.FlowRunAsync(Kernel.Definition("src", "*.csv", "csv", "dst", "{runId}.json",
            mapping: new MappingSpec("explicit", [new("code", "id"), new("libelle", "source.nom")])));
        run.ShouldHaveStatus(RunStatus.Succeeded);
        (run.ReadCount, run.WrittenCount, run.RejectedCount).ShouldBe((250L, 250L, 0L));
        run.StartedAt.ShouldNotBeNull();
        run.EndedAt.ShouldNotBeNull();
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, run.Id + ".json")));
        json.RootElement.GetArrayLength().ShouldBe(250);
        json.RootElement[3].GetProperty("code").GetString().ShouldBe("3");
        json.RootElement[3].GetProperty("libelle").GetString().ShouldBe("Client 3");
        json.RootElement[3].TryGetProperty("montant", out _).ShouldBeFalse();
        (await _k.Runs.LogsAsync(run.Id, 10, default)).Select(l => l.Message).ShouldContain(m => m.StartsWith("Terminée", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_permanent_target_error_fails_the_run_and_publishes_nothing()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        await File.WriteAllTextAsync(Path.Combine(input, "a.json"), "[{\"id\":1,\"client\":{\"nom\":\"A\"}},{\"id\":2,\"client\":\"pas un objet\"},{\"id\":3,\"client\":{\"nom\":\"C\"}}]");
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        // Le 1er enregistrement contient une structure imbriquée : le CSV la refuse (erreur permanente).
        var run = await _k.FlowRunAsync(Kernel.Definition("src", "*.json", "json", "dst", "out.csv", [new("format", "csv")],
            new MappingSpec("explicit", [new("id", "id"), new("client", "client")])));
        run.ShouldHaveStatus(RunStatus.Failed);
        Directory.GetFiles(output).ShouldBeEmpty("tout ou rien : aucun fichier publié");
        run.Error!.ShouldContain("permanente");
    }

    [Fact]
    public async Task Rejects_by_the_target_are_recorded_and_fail_the_run()
    {
        var input = _k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 10);
        using var server = new TestHttpServer((_, body) => body.Contains("\"id\":\"7\"", StringComparison.Ordinal) ? (422, "{\"error\":\"client 7 refusé\"}", null) : (201, "{}", null));
        await _k.LocalAsync("src", input);
        await _k.RestAsync("api", server.BaseUrl);
        var run = await _k.FlowRunAsync(Kernel.Definition("src", "*.csv", "csv", "api", "/clients", []));
        run.ShouldHaveStatus(RunStatus.Failed);
        (run.ReadCount, run.WrittenCount, run.RejectedCount).ShouldBe((10L, 9L, 1L));
        var reject = (await _k.Runs.RejectsAsync(run.Id, 10, default)).Single();
        reject.Stage.ShouldBe("write");
        reject.Reason.ShouldContain("client 7 refusé");
        JsonDocument.Parse(reject.RecordJson!).RootElement.GetProperty("id").GetString().ShouldBe("7");
    }

    [Fact]
    public async Task Transient_target_errors_are_retried_until_success()
    {
        var input = _k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 5);
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        using var server = new TestHttpServer((_, body) => failures.AddOrUpdate(body, 1, (_, n) => n + 1) <= 2 ? (503, "occupé", null) : (200, "{}", null));
        await _k.LocalAsync("src", input);
        await _k.RestAsync("api", server.BaseUrl);
        var run = await _k.FlowRunAsync(Kernel.Definition("src", "*.csv", "csv", "api", "/x", []));
        run.ShouldHaveStatus(RunStatus.Succeeded);
        run.WrittenCount.ShouldBe(5);
        failures.Values.ShouldAllBe(n => n == 3);
    }

    [Fact]
    public async Task Cancellation_takes_effect_after_the_current_batch()
    {
        var input = _k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 2000);
        var gate = new SemaphoreSlim(0);
        using var server = new TestHttpServer((_, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return (200, "{}", null);
        });
        await _k.LocalAsync("src", input);
        await _k.RestAsync("api", server.BaseUrl);
        var flow = await _k.Flows.SaveAsync(Kernel.Admin, null, "lent", "", Kernel.Definition("src", "*.csv", "csv", "api", "/x", [new("mode", "batch")], batch: 100), 0, "t", default);
        var run = await _k.Runs.StartAsync(Kernel.Admin, flow.Id, true, "t", default);
        while (server.RequestCount == 0)
        {
            await Task.Delay(10);
        }

        (await _k.Runs.CancelAsync(Kernel.Admin, run.Id, true, "t", default)).ShouldBeTrue();
        gate.Release(100);
        await _k.Runs.WaitAsync(run.Id);
        var done = (await _k.Runs.GetAsync(run.Id, default))!;
        done.ShouldHaveStatus(RunStatus.Cancelled);
        done.WrittenCount.ShouldBe(100, "le lot en cours est terminé, pas les suivants");
        done.Checkpoint.ShouldNotBeNull();
    }

    [Fact]
    public async Task Interrupted_runs_are_suspended_at_startup()
    {
        await _k.RunRepository.CreateAsync(new RunRecord("r-crash", "f", 1, RunStatus.Pending, "x", null, DateTimeOffset.UtcNow, null, null, 0, 0, 0, 0, null, null, "manual"), default);
        await _k.RunRepository.TransitionAsync("r-crash", RunStatus.Pending, RunStatus.Running, "engine", null, DateTimeOffset.UtcNow, default);
        await _k.RestartRunsAsync();
        (await _k.Runs.GetAsync("r-crash", default))!.Status.ShouldBe(RunStatus.Suspended);
    }

    [Fact]
    public async Task Empty_source_succeeds_with_a_warning()
    {
        await _k.LocalAsync("src", _k.Folder("in"));
        await _k.LocalAsync("dst", _k.Folder("out"));
        var run = await _k.FlowRunAsync(Kernel.Definition("src", "*.csv", "csv", "dst", "out.json"));
        run.ShouldHaveStatus(RunStatus.SucceededWithWarnings);
    }

    [Fact]
    public async Task Missing_connection_fails_cleanly()
    {
        await _k.LocalAsync("src", _k.Folder("in"));
        await _k.LocalAsync("dst", _k.Folder("out"));
        var flow = await _k.Flows.SaveAsync(Kernel.Admin, null, "f", "", Kernel.Definition("src", "*.csv", "csv", "dst", "o.json"), 0, "t", default);
        await _k.LocalAsync("other", _k.Folder("x"));
        // On supprime la connexion cible au niveau base pour simuler une incohérence.
        await new ErpConnect.Storage.Repositories.ConnectionRepository(_k.ConfigDb).DeleteAsync("dst", default);
        var run = await _k.RunToEndAsync(flow.Id);
        run.ShouldHaveStatus(RunStatus.Failed);
        run.Error!.ShouldContain("Connexion introuvable");
    }

    [Fact]
    public async Task Secrets_go_to_the_vault_never_to_the_connection_table()
    {
        var generated = "v-" + Guid.NewGuid().ToString("N"); // générée à l'exécution
        var saved = await _k.RestAsync("api", "https://api.example/", new() { ["token"] = generated });
        saved.SecretFields.ShouldBe(["token"]);
        saved.Settings.Values.ShouldNotContain(generated);
        var dbBytes = await ErpConnect.Tests.Shared.SharedFile.ReadAllBytesAsync(_k.ConfigDb.Path);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Encoding.UTF8.GetString(dbBytes).ShouldNotContain(generated);
        var (_, settings) = await _k.Connections.ResolveAsync("api", default);
        settings.GetSecret("token").ShouldBe(generated);

        // Une modification sans generated conserve le generated ; clearSecrets l'efface.
        var updated = await _k.Connections.SaveAsync(Kernel.Admin, "api", "api", "rest", new Dictionary<string, string?> { ["baseUrl"] = "https://api2.example/" }, new Dictionary<string, string?>(), [], saved.Version, "t", default);
        (await _k.Connections.ResolveAsync("api", default)).Settings.GetSecret("token").ShouldBe(generated);
        await _k.Connections.SaveAsync(Kernel.Admin, "api", "api", "rest", new Dictionary<string, string?> { ["baseUrl"] = "https://api2.example/" }, new Dictionary<string, string?>(), ["token"], updated.Version, "t", default);
        (await _k.Connections.ResolveAsync("api", default)).Settings.GetSecret("token").ShouldBeNull();

        var audit = await _k.Audit.QueryAsync(new Core.Audit.AuditQuery(1, 50, "connections.save", null, null), default);
        audit.Items.Count.ShouldBe(3);
        audit.Items.ShouldAllBe(e => e.DetailsJson == null || !e.DetailsJson.Contains(generated));
    }

    [Fact]
    public async Task Secret_values_sent_as_plain_settings_are_refused()
    {
        var ex = await Should.ThrowAsync<EngineException>(() => _k.Connections.SaveAsync(Kernel.Admin, "api", "api", "rest",
            new Dictionary<string, string?> { ["baseUrl"] = "https://h/", ["token"] = "fuite" }, new Dictionary<string, string?>(), [], 0, "t", default));
        ex.Fields.ShouldContain(f => f.Field == "token");
    }

    [Fact]
    public async Task A_connection_used_by_a_flow_cannot_be_deleted()
    {
        await _k.LocalAsync("src", _k.Folder("in"));
        await _k.LocalAsync("dst", _k.Folder("out"));
        await _k.Flows.SaveAsync(Kernel.Admin, "flux-a", "Flux A", "", Kernel.Definition("src", "*.csv", "csv", "dst", "o.json"), 0, "t", default);
        var ex = await Should.ThrowAsync<EngineException>(() => _k.Connections.DeleteAsync(Kernel.Admin, "src", "t", default));
        ex.Kind.ShouldBe(EngineErrorKind.Conflict);
        ex.Message.ShouldContain("Flux A");
    }

    [Fact]
    public async Task Flow_versions_are_immutable_and_runs_use_the_published_one()
    {
        await _k.LocalAsync("src", _k.Folder("in"));
        await _k.LocalAsync("dst", _k.Folder("out"));
        var v1 = await _k.Flows.SaveAsync(Kernel.Admin, "f1", "F", "", Kernel.Definition("src", "*.csv", "csv", "dst", "v1.json"), 0, "t", default);
        (await Should.ThrowAsync<EngineException>(() => _k.Runs.StartAsync(Kernel.Admin, "f1", false, "t", default))).Kind.ShouldBe(EngineErrorKind.Conflict);
        await _k.Flows.PublishAsync(Kernel.Admin, "f1", 1, "t", default);
        var v2 = await _k.Flows.SaveAsync(Kernel.Admin, "f1", "F", "", Kernel.Definition("src", "*.csv", "csv", "dst", "v2.json"), v1.DraftVersion, "t", default);
        v2.DraftVersion.ShouldBe(2);
        v2.PublishedVersion.ShouldBe(1);
        (await Should.ThrowAsync<EngineException>(() => _k.Flows.SaveAsync(Kernel.Admin, "f1", "F", "", Kernel.Definition("src", "*.csv", "csv", "dst", "x.json"), 1, "t", default)))
            .Kind.ShouldBe(EngineErrorKind.Conflict);
        var run = await _k.Runs.StartAsync(Kernel.Admin, "f1", false, "t", default);
        run.FlowVersion.ShouldBe(1);
        await _k.Runs.WaitAsync(run.Id);

        await using var c = await _k.ConfigDb.OpenAsync(default);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE FlowVersion SET DefinitionJson = '{}' WHERE FlowId = 'f1' AND Version = 1;";
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQuery()).Message.ShouldContain("immuable");
    }

    [Fact]
    public async Task Invalid_flow_definitions_are_refused_with_field_errors()
    {
        var ex = await Should.ThrowAsync<EngineException>(() => _k.Flows.SaveAsync(Kernel.Admin, null, "", "",
            new FlowDefinition(new SourceSpec("", "", []), new TargetSpec("absent", "x", "append", []), new MappingSpec("explicit", [new("a", "b c"), new("a", "d")]), 0), 0, "t", default));
        ex.Fields.Select(f => f.Field).ShouldBe(
            ["source.connectionId", "source.resource", "target.mode", "batchSize", "mapping.fields", "mapping.fields[0].expression", "name", "target.connectionId"], ignoreOrder: true);
    }
}
