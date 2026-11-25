using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Flows;

public enum RecordFate
{
    Output,
    Filtered,
    Rejected,
}

/// <summary>Résultat de transformation d'un enregistrement : sortie, écarté par un filtre, ou rejet motivé.</summary>
public readonly record struct MapResult(Record? Output, string? RejectReason, RecordFate Fate = RecordFate.Output)
{
    public static MapResult Reject(string reason) => new(null, reason, RecordFate.Rejected);

    public static MapResult Filtered { get; } = new(null, null, RecordFate.Filtered);
}

/// <summary>Correspondance des champs (identité ou liste de champs cibles calculés par expression).</summary>
public sealed class RecordMapper
{
    private readonly bool _identity;
    private readonly (string Target, CompiledExpression Expr)[] _fields;

    private RecordMapper(bool identity, (string, CompiledExpression)[] fields)
    {
        _identity = identity;
        _fields = fields;
    }

    public IReadOnlyList<(string Target, CompiledExpression Expression)> Fields => _fields;

    public static RecordMapper Compile(MappingSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Mode == "identity")
        {
            return new RecordMapper(true, []);
        }

        var fields = spec.Fields.Select(f => (f.Target, ExpressionCompiler.TryCompile(f.Expression, out var err) ?? throw new FlowValidationException([new("mapping", $"{f.Target} : {err}")]))).ToArray();
        return new RecordMapper(false, fields);
    }

    public MapResult Map(Record source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_identity)
        {
            return new MapResult(source, null);
        }

        var output = new Record();
        foreach (var (target, expr) in _fields)
        {
            try
            {
                output.Set(target, expr.Evaluate(source));
            }
            catch (ExpressionException ex)
            {
                return MapResult.Reject($"{target} : {ex.Message}");
            }
            catch (ConnectorException ex)
            {
                return MapResult.Reject($"{target} : {ex.Message}");
            }
        }

        return new MapResult(output, null);
    }
}

/// <summary>Chaîne complète d'un flux : blocs de transformation puis correspondance. Compilée une fois par exécution.</summary>
public sealed class RecordPipeline : IDisposable
{
    private readonly Func<Record, MapResult>[] _steps;
    private readonly RecordMapper _mapper;
    private readonly List<IDisposable> _resources;

    private RecordPipeline(Func<Record, MapResult>[] steps, RecordMapper mapper, List<IDisposable> resources)
    {
        _steps = steps;
        _mapper = mapper;
        _resources = resources;
    }

    /// <summary>Libère les interpréteurs de script. Un pipeline n'est pas sûr entre threads (un enregistrement à la fois).</summary>
    public void Dispose()
    {
        foreach (var r in _resources)
        {
            r.Dispose();
        }

        _resources.Clear();
    }

    public static RecordPipeline Compile(FlowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = definition.AllSteps.SelectMany((s, i) => ValidateStep(s).Select(e => new FlowError($"steps[{i}].{e.Field}", e.Message))).ToList();
        if (errors.Count > 0)
        {
            throw new FlowValidationException(errors);
        }

        var resources = new List<IDisposable>();
        try
        {
            return new RecordPipeline(definition.AllSteps.Select(s => CompileStep(s, resources)).ToArray(), RecordMapper.Compile(definition.Mapping), resources);
        }
        catch
        {
            resources.ForEach(r => r.Dispose());
            throw;
        }
    }

    public MapResult Process(Record source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var current = source;
        if (_steps.Length > 0)
        {
            current = Copy(source);
            foreach (var step in _steps)
            {
                var r = step(current);
                if (r.Fate != RecordFate.Output)
                {
                    return r;
                }

                current = r.Output ?? current; // un script renvoie un nouvel enregistrement ; les autres blocs modifient en place
            }
        }

        return _mapper.Map(current);
    }

    private static Record Copy(Record r)
    {
        var copy = new Record();
        foreach (var (k, v) in r)
        {
            copy.Set(k, v);
        }

        return copy;
    }

    public static IReadOnlyList<FlowError> ValidateStep(StepSpec step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var errors = new List<FlowError>();
        void Expr(string? e)
        {
            if (ExpressionCompiler.TryCompile(e, out var err) is null)
            {
                errors.Add(new("expression", err!));
            }
        }

        void Field(string? f, string name = "field")
        {
            if (string.IsNullOrWhiteSpace(f))
            {
                errors.Add(new(name, "Nom de champ obligatoire."));
            }
        }

        switch (step.Kind)
        {
            case "filter":
                Expr(step.Expression);
                break;
            case "set":
                Field(step.Field);
                Expr(step.Expression);
                break;
            case "rename":
                Field(step.Field);
                Field(step.Target, "target");
                break;
            case "remove":
                Field(step.Field);
                break;
            case "validate":
                Expr(step.Expression);
                if (string.IsNullOrWhiteSpace(step.Message))
                {
                    errors.Add(new("message", "Motif de rejet obligatoire."));
                }

                break;
            case "script":
                if (ScriptStep.Validate(step.Expression) is { } scriptError)
                {
                    errors.Add(new("expression", scriptError));
                }

                break;
            default:
                errors.Add(new("kind", "Valeurs admises : filter, set, rename, remove, validate, script."));
                break;
        }

        return errors;
    }

    private static Func<Record, MapResult> CompileStep(StepSpec step, List<IDisposable> resources)
    {
        static MapResult Guard(string label, Func<MapResult> f)
        {
            try
            {
                return f();
            }
            catch (ExpressionException ex)
            {
                return MapResult.Reject($"{label} : {ex.Message}");
            }
            catch (ConnectorException ex)
            {
                return MapResult.Reject($"{label} : {ex.Message}");
            }
        }

        var ok = new MapResult(null, null);
        switch (step.Kind)
        {
            case "filter":
            {
                var e = ExpressionCompiler.Compile(step.Expression);
                return r => Guard("filtre", () => ExpressionCompiler.Values.ToBool(e.Evaluate(r)) ? ok : MapResult.Filtered);
            }

            case "set":
            {
                var e = ExpressionCompiler.Compile(step.Expression);
                var field = step.Field!;
                return r => Guard(field, () =>
                {
                    r.Set(field, e.Evaluate(r));
                    return ok;
                });
            }

            case "rename":
            {
                var from = step.Field!;
                var to = step.Target!;
                return r =>
                {
                    if (r.TryGet(from, out var v))
                    {
                        r.Remove(from);
                        r.Set(to, v);
                    }

                    return ok;
                };
            }

            case "remove":
            {
                var names = step.Field!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return r =>
                {
                    foreach (var n in names)
                    {
                        r.Remove(n);
                    }

                    return ok;
                };
            }

            case "script":
            {
                ScriptStep script;
                try
                {
                    script = ScriptStep.Compile(step.Expression);
                }
                catch (ScriptException ex)
                {
                    throw new FlowValidationException([new("script", ex.Message)]);
                }

                resources.Add(script);
                return script.Apply;
            }

            default:
            {
                var e = ExpressionCompiler.Compile(step.Expression);
                var message = step.Message!;
                return r => Guard("contrôle", () => ExpressionCompiler.Values.ToBool(e.Evaluate(r)) ? ok : MapResult.Reject(message));
            }
        }
    }
}
