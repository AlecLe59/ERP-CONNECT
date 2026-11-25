using System.IO.Compression;
using System.Text.Json;
using ErpConnect.Core.Runs;
using ErpConnect.Flows;

namespace ErpConnect.Engine.Tests;

public sealed class Phase4KernelTests : IAsyncLifetime
{
    private Kernel _k = null!;

    public async Task InitializeAsync() => _k = await Kernel.StartAsync();

    public async Task DisposeAsync() => await _k.DisposeAsync();

    private static FlowDefinition WithSteps(FlowDefinition d, params StepSpec[] steps) => d with { Steps = steps };

    [Fact]
    public async Task Filtered_records_are_counted_apart_and_steps_feed_the_mapping()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 10);
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        var def = WithSteps(
            Kernel.Definition("src", "*.csv", "csv", "dst", "out.json", mapping: new MappingSpec("explicit",
                [new("code", "padLeft([id], 4, '0')"), new("ttc", "round(toNumber([montant]) * 1.2, 2)"), new("label", "[label]")])),
            new StepSpec("filter", Expression: "toInteger([id]) % 2 = 0"),
            new StepSpec("set", Field: "label", Expression: "upper([nom])"));
        var run = await _k.FlowRunAsync(def);
        run.ShouldHaveStatus(RunStatus.Succeeded);
        (run.ReadCount, run.WrittenCount, run.SkippedCount, run.RejectedCount).ShouldBe((10L, 5L, 5L, 0L));
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "out.json"))).RootElement;
        json.GetArrayLength().ShouldBe(5);
        json[1].GetProperty("code").GetString().ShouldBe("0002");
        json[1].GetProperty("ttc").GetDecimal().ShouldBe(3m);
        json[1].GetProperty("label").GetString().ShouldBe("CLIENT 2");
        (await _k.Runs.LogsAsync(run.Id, 10, default)).ShouldContain(l => l.Message.Contains("5 filtré(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validation_rejects_fail_the_run_and_publish_nothing()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 6, i => i == 4 ? "4;;1" : $"{i};Client {i};1");
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        var run = await _k.FlowRunAsync(WithSteps(Kernel.Definition("src", "*.csv", "csv", "dst", "out.json"),
            new StepSpec("validate", Expression: "not isEmpty([nom])", Message: "Nom obligatoire")));
        run.ShouldHaveStatus(RunStatus.Failed);
        run.RejectedCount.ShouldBe(1);
        var reject = (await _k.Runs.RejectsAsync(run.Id, 10, default)).Single();
        (reject.Stage, reject.Reason, reject.RecordIndex).ShouldBe(("mapping", "Nom obligatoire", 4));
        Directory.GetFiles(output).ShouldBeEmpty();
    }

    [Fact]
    public async Task Zip_source_to_zip_target()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        using (var zip = ZipFile.Open(Path.Combine(input, "lot.zip"), ZipArchiveMode.Create))
        {
            foreach (var (name, from) in new[] { ("a.csv", 0), ("b.csv", 3) })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("id;nom\n" + string.Join('\n', Enumerable.Range(from, 3).Select(i => $"{i};n{i}")) + "\n");
            }
        }

        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        var def = new FlowDefinition(
            new SourceSpec("src", "*.zip", [new("format", "csv"), new("archive", "zip"), new("entryPattern", "*.csv")]),
            new TargetSpec("dst", "export.zip", "createNew", [new("format", "json"), new("archive", "zip"), new("entryName", "clients.json")]),
            new MappingSpec("identity", []));
        var run = await _k.FlowRunAsync(def);
        run.ShouldHaveStatus(RunStatus.Succeeded);
        run.WrittenCount.ShouldBe(6);
        using var result = ZipFile.OpenRead(Path.Combine(output, "export.zip"));
        using var s = result.GetEntry("clients.json")!.Open();
        JsonDocument.Parse(s).RootElement.GetArrayLength().ShouldBe(6);
    }

    [Fact]
    public async Task Preview_transforms_a_sample_without_writing_anything()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 50);
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        var preview = new PreviewService(_k.Connections);
        var def = WithSteps(
            Kernel.Definition("src", "*.csv", "csv", "dst", "out.json", mapping: new MappingSpec("explicit", [new("n", "toInteger([id]) * 10")])),
            new StepSpec("filter", Expression: "[id] <> '1'"),
            new StepSpec("validate", Expression: "[id] <> '2'", Message: "pas le 2"));
        var r = await preview.PreviewAsync(def, 5, default);
        r.SourceFields.ShouldBe(["id", "nom", "montant"]);
        r.Rows.Count.ShouldBe(5);
        r.Rows.Select(x => x.Fate).ShouldBe([RecordFate.Output, RecordFate.Filtered, RecordFate.Rejected, RecordFate.Output, RecordFate.Output]);
        r.Rows[3].OutputJson.ShouldBe("{\"n\":30}");
        r.Rows[2].Reason.ShouldBe("pas le 2");
        Directory.GetFiles(output).ShouldBeEmpty();
        (await _k.Runs.QueryAsync(new ErpConnect.Core.Flows.RunQuery(null, null, 1, 10), default)).Total.ShouldBe(0);
    }

    [Fact]
    public async Task Preview_refuses_invalid_expressions_and_out_of_range_limits()
    {
        var preview = new PreviewService(_k.Connections);
        var def = WithSteps(Kernel.Definition("src", "*.csv", "csv", "dst", "out.json"), new StepSpec("filter", Expression: "1 +"));
        var ex = await Should.ThrowAsync<EngineException>(() => preview.PreviewAsync(def, 5, default));
        ex.Fields.ShouldContain(f => f.Field == "steps[0].expression");
        await Should.ThrowAsync<EngineException>(() => preview.PreviewAsync(Kernel.Definition("src", "*", "csv", "dst", "x"), 500, default));
    }
}
