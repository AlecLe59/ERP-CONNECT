using System.Diagnostics;
using System.Text.Json;
using ErpConnect.Connectors.Sdk;
using ErpConnect.Core.Runs;
using ErpConnect.Flows;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Engine.Tests;

/// <summary>Bloc script : comportement attendu, puis scripts malveillants (chaque attaque doit échouer proprement et vite).</summary>
public class ScriptTests
{
    private static readonly ScriptLimits Fast = ScriptLimits.Default with { TimeoutPerRecord = TimeSpan.FromMilliseconds(300) };

    private static ScriptStep Script(string code, ScriptLimits? limits = null) => ScriptStep.Compile(code, limits ?? Fast);

    private static Record Sample() => new Record().Set("id", 7L).Set("nom", "Dupont").Set("montant", 12.5m).Set("tags", new List<object?> { "a", "b" })
        .Set("adresse", new Record().Set("ville", "Lille"));

    [Fact]
    public void Transform_can_compute_add_and_remove_fields()
    {
        using var s = Script("""
            function transform(r) {
              r.code = 'C' + String(r.id).padStart(4, '0');
              r.ttc = Math.round(r.montant * 1.2 * 100) / 100;
              r.ville = r.adresse.ville.toUpperCase();
              r.nbTags = r.tags.length;
              delete r.adresse;
              return r;
            }
            """);
        var r = s.Apply(Sample());
        r.Fate.ShouldBe(RecordFate.Output);
        r.Output!["code"].ShouldBe("C0007");
        r.Output["ttc"].ShouldBe(15m);
        r.Output["ville"].ShouldBe("LILLE");
        r.Output["nbTags"].ShouldBe(2L);
        r.Output.Contains("adresse").ShouldBeFalse();
    }

    [Fact]
    public void Null_filters_and_throw_rejects_with_the_message()
    {
        using var s = Script("""
            function transform(r) {
              if (r.id < 0) return null;
              if (!r.nom) throw new Error('Nom obligatoire');
              return { id: r.id };
            }
            """);
        s.Apply(new Record().Set("id", -1L)).Fate.ShouldBe(RecordFate.Filtered);
        var rejected = s.Apply(new Record().Set("id", 1L));
        rejected.Fate.ShouldBe(RecordFate.Rejected);
        rejected.RejectReason!.ShouldContain("Nom obligatoire");
        s.Apply(new Record().Set("id", 1L).Set("nom", "x")).Output!.Names.ShouldBe(["id"]);
    }

    [Theory]
    [InlineData("function transform(r) { return 42; }")]
    [InlineData("function transform(r) { return [r]; }")]
    [InlineData("function transform(r) { return 'texte'; }")]
    public void Non_object_results_are_rejects(string code)
    {
        using var s = Script(code);
        s.Apply(Sample()).Fate.ShouldBe(RecordFate.Rejected);
    }

    [Theory]
    [InlineData("", "vide")]
    [InlineData("var x = 1;", "transform")]
    [InlineData("function transform(r) { return r; ", "syntaxe")]
    public void Invalid_scripts_are_refused_at_save_time(string code, string expected)
    {
        ScriptStep.Validate(code)!.ShouldContain(expected);
        var def = new FlowDefinition(new SourceSpec("s", "a", []), new TargetSpec("t", "b", "createNew", []), new MappingSpec("identity", []), Steps: [new StepSpec("script", Expression: code)]);
        def.Validate().ShouldContain(e => e.Field == "steps[0].expression");
    }

    [Fact]
    public void Oversized_script_is_refused()
    {
        ScriptStep.Validate("function transform(r) { return r; }\n//" + new string('x', ScriptLimits.MaxSourceLength)).ShouldNotBeNull();
    }

    [Fact]
    public void State_does_not_leak_between_records_through_the_record_but_globals_are_shared_and_documented()
    {
        using var s = Script("""
            var n = 0;
            function transform(r) { n++; r.n = n; r.id = 0; return r; }
            """);
        var src = Sample();
        s.Apply(src).Output!["n"].ShouldBe(1L);
        s.Apply(src).Output!["n"].ShouldBe(2L);
        src["id"].ShouldBe(7L); // l'enregistrement source n'est jamais modifié : le script reçoit une copie JSON
    }

    [Fact]
    public void Limits_apply_per_record_not_per_run()
    {
        // Environ 60 000 instructions par enregistrement, plafond 100 000 : cumulées sur 50 enregistrements, elles le dépasseraient 30 fois.
        using var s = Script("function transform(r) { var t = 0; for (var i = 0; i < 20000; i++) { t += i; } r.t = t; return r; }",
            Fast with { MaxStatements = 100_000, TimeoutPerRecord = TimeSpan.FromSeconds(10) });
        for (var i = 0; i < 50; i++)
        {
            s.Apply(Sample()).Fate.ShouldBe(RecordFate.Output);
        }

        using var tight = Script("function transform(r) { var t = 0; for (var i = 0; i < 20000; i++) { t += i; } r.t = t; return r; }",
            Fast with { MaxStatements = 10_000, TimeoutPerRecord = TimeSpan.FromSeconds(10) });
        Should.Throw<ConnectorException>(() => tight.Apply(Sample())); // un seul enregistrement dépasse 10 000 : le plafond est bien actif
    }

    [Fact]
    public void Dates_cross_as_iso_strings()
    {
        using var s = Script("function transform(r) { r.annee = Number(r.d.substring(0, 4)); return r; }");
        s.Apply(new Record().Set("d", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero))).Output!["annee"].ShouldBe(2026L);
    }

    // ---- Scripts malveillants ----

    private static void ShouldStop(ScriptStep s, string expected)
    {
        var sw = Stopwatch.StartNew();
        var ex = Should.Throw<ConnectorException>(() => s.Apply(Sample()));
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        ex.Kind.ShouldBe(ErrorKind.Permanent);
        ex.Message.ShouldContain(expected);
    }

    [Fact]
    public void Infinite_loop_is_stopped()
    {
        using var s = Script("function transform(r) { while (true) { } }", Fast with { MaxStatements = int.MaxValue });
        ShouldStop(s, "délai");
    }

    [Fact]
    public void Statement_flood_is_stopped()
    {
        using var s = Script("function transform(r) { for (;;) { r.x = 1; } }", Fast with { TimeoutPerRecord = TimeSpan.FromSeconds(30), MaxStatements = 10_000 });
        ShouldStop(s, "instructions");
    }

    [Fact]
    public void Infinite_loop_at_top_level_is_refused_at_compile()
    {
        Should.Throw<ScriptException>(() => Script("while (true) {} function transform(r) { return r; }"));
    }

    [Fact]
    public void Memory_bomb_is_stopped()
    {
        using var s = Script("function transform(r) { var a = []; var s = 'xxxxxxxxxxxxxxxx'; for (;;) { s = s + s; a.push(s); } }",
            Fast with { TimeoutPerRecord = TimeSpan.FromSeconds(20), MaxStatements = int.MaxValue });
        var ex = Should.Throw<ConnectorException>(() => s.Apply(Sample()));
        ex.Message.ShouldStartWith("Script arrêté");
    }

    [Fact]
    public void Huge_array_is_stopped()
    {
        using var s = Script("function transform(r) { var a = new Array(1e9).fill(0); return r; }");
        Should.Throw<ConnectorException>(() => s.Apply(Sample())).Message.ShouldStartWith("Script arrêté");
    }

    [Fact]
    public void Deep_recursion_is_stopped()
    {
        using var s = Script("function f(n) { return f(n + 1) + 1; } function transform(r) { f(0); return r; }");
        ShouldStop(s, "récursion");
    }

    [Theory]
    [InlineData("eval('1+1')")]
    [InlineData("new Function('return 1')()")]
    [InlineData("(function(){}).constructor('return this')()")]
    [InlineData("setTimeout(function(){}, 1)")]
    public void Dynamic_code_and_host_timers_are_unavailable(string expr)
    {
        using var s = Script($"function transform(r) {{ {expr}; return r; }}");
        s.Apply(Sample()).Fate.ShouldBe(RecordFate.Rejected);
    }

    [Theory]
    [InlineData("System")]
    [InlineData("importNamespace")]
    [InlineData("clr")]
    [InlineData("require")]
    [InlineData("process")]
    [InlineData("fetch")]
    [InlineData("XMLHttpRequest")]
    [InlineData("Deno")]
    public void No_host_object_is_reachable(string name)
    {
        using var s = Script($"function transform(r) {{ return {{ t: typeof {name} }}; }}");
        s.Apply(Sample()).Output!["t"].ShouldBe("undefined");
    }

    [Fact]
    public void Record_is_plain_json_without_clr_type_access()
    {
        using var s = Script("function transform(r) { return { gt: typeof r.GetType, proto: Object.getPrototypeOf(r) === Object.prototype, ctor: r.constructor.name }; }");
        var o = s.Apply(Sample()).Output!;
        (o["gt"], o["proto"], o["ctor"]).ShouldBe(("undefined", true, "Object"));
    }

    [Fact]
    public void Import_and_modules_are_unavailable()
    {
        Should.Throw<ScriptException>(() => Script("import fs from 'fs'; function transform(r) { return r; }"));
        using var s = Script("function transform(r) { return import('fs'); }");
        s.Apply(Sample()).Fate.ShouldBe(RecordFate.Rejected);
    }

    [Fact]
    public void Catastrophic_regex_is_stopped()
    {
        using var s = Script("function transform(r) { /^(a+)+$/.test('a'.repeat(40) + '!'); return r; }");
        Should.Throw<ConnectorException>(() => s.Apply(Sample())).Message.ShouldStartWith("Script arrêté");
    }

    [Fact]
    public void Too_deep_result_is_a_reject()
    {
        using var s = Script("function transform(r) { var o = {}; var c = o; for (var i = 0; i < 40; i++) { c.x = {}; c = c.x; } return o; }");
        s.Apply(Sample()).Fate.ShouldBe(RecordFate.Rejected);
    }

    [Fact]
    public void Prototype_pollution_stays_inside_the_sandbox()
    {
        using var a = Script("function transform(r) { Object.prototype.pwned = 1; return r; }");
        using var b = Script("function transform(r) { return { p: typeof ({}).pwned }; }");
        a.Apply(Sample());
        b.Apply(Sample()).Output!["p"].ShouldBe("undefined");
    }

    [Fact]
    public void Script_error_message_is_truncated()
    {
        using var s = Script("function transform(r) { throw new Error('x'.repeat(10000)); }");
        s.Apply(Sample()).RejectReason!.Length.ShouldBeLessThan(400);
    }
}

public sealed class ScriptKernelTests : IAsyncLifetime
{
    private Kernel _k = null!;

    public async Task InitializeAsync() => _k = await Kernel.StartAsync();

    public async Task DisposeAsync() => await _k.DisposeAsync();

    [Fact]
    public async Task Script_step_runs_inside_a_flow_with_filter_and_reject()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 10);
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        var def = Kernel.Definition("src", "*.csv", "csv", "dst", "out.json") with
        {
            Steps = [new StepSpec("script", Expression: "function transform(r) { var id = Number(r.id); if (id % 2) return null; return { id: id, nom: r.nom.toUpperCase() }; }")],
        };
        var run = await _k.FlowRunAsync(def);
        run.ShouldHaveStatus(RunStatus.Succeeded);
        (run.ReadCount, run.WrittenCount, run.SkippedCount).ShouldBe((10L, 5L, 5L));
        var text = await File.ReadAllTextAsync(Path.Combine(output, "out.json"));
        var json = JsonDocument.Parse(text).RootElement;
        text.ShouldNotContain("montant");
        json[0].GetProperty("id").GetInt64().ShouldBe(0);
        json[1].GetProperty("nom").GetString().ShouldBe("CLIENT 2");
    }

    [Fact]
    public async Task Runaway_script_fails_the_run_without_publishing()
    {
        var input = _k.Folder("in");
        var output = _k.Folder("out");
        await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 3);
        await _k.LocalAsync("src", input);
        await _k.LocalAsync("dst", output);
        var def = Kernel.Definition("src", "*.csv", "csv", "dst", "out.json") with
        {
            Steps = [new StepSpec("script", Expression: "function transform(r) { while (true) {} }")],
        };
        var run = await _k.FlowRunAsync(def);
        run.ShouldHaveStatus(RunStatus.Failed);
        run.Error!.ShouldContain("Script arrêté");
        Directory.GetFiles(output).ShouldBeEmpty();
    }
}

public class ScriptPipelineTests
{
    [Fact]
    public void Later_steps_and_mapping_see_the_script_output()
    {
        var def = new FlowDefinition(new SourceSpec("s", "a", []), new TargetSpec("t", "b", "createNew", []),
            new MappingSpec("explicit", [new("label", "[code] & '-' & [nom]")]), Steps:
            [
                new StepSpec("script", Expression: "function transform(r) { return { code: 'X' + r.id, nom: r.nom }; }"),
                new StepSpec("validate", Expression: "not isEmpty([code])", Message: "code"),
            ]);
        using var p = RecordPipeline.Compile(def);
        p.Process(new Record().Set("id", 3L).Set("nom", "A")).Output!["label"].ShouldBe("X3-A");
    }
}

public class ScriptParserAttackTests
{
    [Theory]
    [InlineData("(", ")")]
    [InlineData("[", "]")]
    [InlineData("{a:", "}")]
    [InlineData("-", "")]
    [InlineData("!", "")]
    public void Deeply_nested_source_does_not_crash_the_process(string open, string close)
    {
        var depth = (ScriptLimits.MaxSourceLength - 100) / (open.Length + close.Length);
        var code = "var x = " + string.Concat(Enumerable.Repeat(open, depth)) + "1" + string.Concat(Enumerable.Repeat(close, depth)) + "; function transform(r) { return r; }";
        ScriptStep.Validate(code).ShouldNotBeNull();
    }
}
