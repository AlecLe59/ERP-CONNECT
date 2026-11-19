using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Flows;

/// <summary>Résultat de transformation d'un enregistrement : sortie, ou rejet motivé.</summary>
public readonly record struct MapResult(Record? Output, string? RejectReason)
{
    public static MapResult Reject(string reason) => new(null, reason);
}

/// <summary>Transformation compilée d'un flux (mapping des champs). Sans état : réutilisable pour tous les lots.</summary>
public sealed class RecordMapper
{
    private readonly bool _identity;
    private readonly (string Target, CompiledExpression Expr)[] _fields;

    private RecordMapper(bool identity, (string, CompiledExpression)[] fields)
    {
        _identity = identity;
        _fields = fields;
    }

    public static RecordMapper Compile(MappingSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Mode == "identity")
        {
            return new RecordMapper(true, []);
        }

        var fields = spec.Fields.Select(f => (f.Target, ExpressionCompiler.TryCompile(f.Expression, out var err) ?? throw new FlowValidationException([new("mapping", err!)]))).ToArray();
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
