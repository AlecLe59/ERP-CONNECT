using ErpConnect.Flows;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Engine.Tests;

public class ExpressionTests
{
    private static Record Src() => new Record()
        .Set("code", " a-12 ")
        .Set("qty", 3L)
        .Set("price", 2.5m)
        .Set("name", "Dupont")
        .Set("empty", "")
        .Set("nothing", null)
        .Set("date", "2026-11-19")
        .Set("nested", new Record().Set("city", "Lille"));

    private static object? Eval(string e) => ExpressionCompiler.Compile(e).Evaluate(Src());

    [Theory]
    [InlineData("1 + 2 * 3", 7L)]
    [InlineData("(1 + 2) * 3", 9L)]
    [InlineData("10 - 4 - 3", 3L)]
    [InlineData("7 % 3", 1L)]
    [InlineData("[qty] * 2", 6L)]
    [InlineData("len('abc') + 1", 4L)]
    public void Arithmetic_respects_precedence(string e, long expected) => Eval(e).ShouldBe(expected);

    [Theory]
    [InlineData("1 < 2 and 2 < 3", true)]
    [InlineData("1 > 2 or not (2 > 3)", true)]
    [InlineData("not 1 = 1 or 1 = 1", true)]
    [InlineData("[qty] >= 3 and [name] = 'Dupont'", true)]
    [InlineData("[name] <> 'Dupont'", false)]
    [InlineData("[nothing] = null", true)]
    public void Logic_and_comparison(string e, bool expected) => Eval(e).ShouldBe(expected);

    [Theory]
    [InlineData("upper(trim([code]))", "A-12")]
    [InlineData("'Bonjour ' & [name]", "Bonjour Dupont")]
    [InlineData("'l''été'", "l'été")]
    [InlineData("substr('abcdef', 2, 3)", "bcd")]
    [InlineData("left('abcdef', 2) & right('abcdef', 2)", "abef")]
    [InlineData("padLeft('42', 6, '0')", "000042")]
    [InlineData("replace('a.b.c', '.', '/')", "a/b/c")]
    [InlineData("split('a;b;c', ';', 2)", "b")]
    [InlineData("coalesce([nothing], [empty], 'défaut')", "défaut")]
    [InlineData("if([qty] > 2, 'beaucoup', 'peu')", "beaucoup")]
    [InlineData("switch([name], 'Martin', 'M', 'Dupont', 'D', '?')", "D")]
    [InlineData("switch('x', 'a', 1, '?')", "?")]
    [InlineData("regexReplace('A-12', '[^0-9]', '')", "12")]
    [InlineData("nested.city", "Lille")]
    [InlineData("source.nested.city", "Lille")]
    [InlineData("source.name", "Dupont")]
    [InlineData("formatDate(toDate([date]), 'dd/MM/yyyy')", "19/11/2026")]
    [InlineData("formatDate(addDays(toDate([date]), 13), 'yyyy-MM-dd')", "2026-12-02")]
    [InlineData("toText(round(2.345, 2))", "2.35")]
    public void Functions_return_expected_text(string e, string expected) => Eval(e).ShouldBe(expected);

    [Fact]
    public void Decimal_arithmetic_is_exact()
    {
        Eval("[price] * [qty]").ShouldBe(7.5m);
        Eval("0.1 + 0.2").ShouldBe(0.3m);
        Eval("toNumber('1.234,56', ',')").ShouldBe(1234.56m);
        Eval("round(2.5)").ShouldBe(3m);
        Eval("toInteger('12.9')").ShouldBe(12L);
        Eval("9223372036854775807 + 1").ShouldBe(9223372036854775808m, "dépassement d'entier : bascule exacte en décimal");
    }

    [Fact]
    public void Null_propagates_in_text_functions()
    {
        Eval("upper([nothing])").ShouldBeNull();
        Eval("isNull([nothing]) and isEmpty([empty])").ShouldBe(true);
        Eval("[absent]").ShouldBeNull();
    }

    [Fact]
    public void Fields_read_are_reported()
    {
        ExpressionCompiler.Compile("concat([name], [qty], source.code)").Fields.ShouldBe(["name", "qty", "code"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("upper(")]
    [InlineData("inconnue(1)")]
    [InlineData("upper(1, 2)")]
    [InlineData("'non fermé")]
    [InlineData("[champ")]
    [InlineData("1 2")]
    [InlineData("")]
    public void Syntax_errors_are_reported_at_compile_time(string e)
    {
        ExpressionCompiler.TryCompile(e, out var error).ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("toNumber('abc')")]
    [InlineData("error('motif métier')")]
    [InlineData("79228162514264337593543950335 * 10")]
    [InlineData("toInteger(99999999999999999999999)")]
    [InlineData("toDate('pas une date')")]
    [InlineData("[name] * 2")]
    public void Runtime_errors_raise_expression_exception(string e)
    {
        var compiled = ExpressionCompiler.Compile(e);
        Should.Throw<ExpressionException>(() => compiled.Evaluate(Src()));
    }

    [Fact]
    public void Limits_on_length_and_depth()
    {
        ExpressionCompiler.TryCompile(new string('1', ExpressionCompiler.MaxLength + 1), out _).ShouldBeNull();
        var deep = new string('(', 200) + "1" + new string(')', 200);
        ExpressionCompiler.TryCompile(deep, out var error).ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void String_growth_is_bounded()
    {
        ((string)ExpressionCompiler.Compile("padLeft('x', 2000000)").Evaluate(Src())!).Length.ShouldBe(ExpressionCompiler.MaxStringLength);
        var e = ExpressionCompiler.Compile("padLeft('x', 1048576) & padLeft('y', 1048576)");
        Should.Throw<ExpressionException>(() => e.Evaluate(Src()));
    }

    [Fact]
    public void Bracketed_name_is_literal_so_dotted_csv_headers_work()
    {
        ExpressionCompiler.Compile("[a.b]").Evaluate(new Record().Set("a.b", 1L)).ShouldBe(1L);
    }

    [Fact]
    public void Catastrophic_regex_times_out_instead_of_hanging()
    {
        var e = ExpressionCompiler.Compile("regexMatch('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!', '^(a+)+$')");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Should.Throw<ExpressionException>(() => e.Evaluate(Src()));
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Catalog_lists_every_function_with_signature()
    {
        ExpressionCompiler.FunctionCatalog.Count.ShouldBeGreaterThanOrEqualTo(35);
        ExpressionCompiler.FunctionCatalog.ShouldAllBe(f => f.Signature.StartsWith(f.Name + "(", StringComparison.Ordinal));
    }
}

public class PipelineTests
{
    private static FlowDefinition Def(StepSpec[] steps, MappingSpec? mapping = null) => new(
        new SourceSpec("s", "in.csv", []), new TargetSpec("t", "out.csv", "createNew", []), mapping ?? new MappingSpec("identity", []), 100, 10, steps);

    [Fact]
    public void Steps_run_in_order_then_mapping()
    {
        var p = RecordPipeline.Compile(Def(
        [
            new StepSpec("set", Field: "full", Expression: "[first] & ' ' & [last]"),
            new StepSpec("rename", Field: "full", Target: "name"),
            new StepSpec("remove", Field: "first, last"),
            new StepSpec("validate", Expression: "len([name]) > 3", Message: "Nom trop court"),
        ]));
        var src = new Record().Set("first", "Jean").Set("last", "Dupont").Set("age", 40L);
        var r = p.Process(src);
        r.Fate.ShouldBe(RecordFate.Output);
        r.Output!.Names.ShouldBe(["age", "name"]);
        r.Output["name"].ShouldBe("Jean Dupont");
        src.Names.ShouldBe(["first", "last", "age"], "la source n'est jamais modifiée");
    }

    [Fact]
    public void Filter_discards_and_validate_rejects_with_reason()
    {
        var p = RecordPipeline.Compile(Def(
        [
            new StepSpec("filter", Expression: "[active] = 'oui'"),
            new StepSpec("validate", Expression: "not isEmpty([email])", Message: "E-mail manquant"),
        ], new MappingSpec("fields", [new FieldMap("mail", "lower([email])")])));
        p.Process(new Record().Set("active", "non").Set("email", "a@b")).Fate.ShouldBe(RecordFate.Filtered);
        var rejected = p.Process(new Record().Set("active", "oui").Set("email", ""));
        rejected.Fate.ShouldBe(RecordFate.Rejected);
        rejected.RejectReason.ShouldBe("E-mail manquant");
        p.Process(new Record().Set("active", "oui").Set("email", "A@B")).Output!["mail"].ShouldBe("a@b");
    }

    [Fact]
    public void Runtime_error_in_step_becomes_a_reject_not_a_crash()
    {
        var p = RecordPipeline.Compile(Def([new StepSpec("set", Field: "x", Expression: "toNumber([v])")]));
        var r = p.Process(new Record().Set("v", "abc"));
        r.Fate.ShouldBe(RecordFate.Rejected);
        r.RejectReason!.ShouldStartWith("x :");
    }

    [Fact]
    public void Invalid_steps_are_refused_with_field_paths()
    {
        var ex = Should.Throw<FlowValidationException>(() => RecordPipeline.Compile(Def(
        [
            new StepSpec("filter", Expression: "1 +"),
            new StepSpec("validate", Expression: "true"),
            new StepSpec("teleport"),
        ])));
        ex.Errors.Select(e => e.Field).ShouldBe(["steps[0].expression", "steps[1].message", "steps[2].kind"]);
    }
}
