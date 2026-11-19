namespace ErpConnect.Flows;

public sealed class ExpressionException : Exception
{
    public ExpressionException(string message)
        : base(message)
    {
    }

    public ExpressionException()
    {
    }

    public ExpressionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Expression compilée, évaluée sur un enregistrement source.</summary>
public abstract class CompiledExpression
{
    public abstract object? Evaluate(ErpConnect.Connectors.Sdk.Record source);
}

/// <summary>
/// Phase 3 : une expression est un chemin de champ source (« client.nom »), optionnellement préfixé « source. ».
/// La Phase 4 remplace ce compilateur par le langage d'expressions complet.
/// </summary>
public static class ExpressionCompiler
{
    public static CompiledExpression? TryCompile(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Expression vide.";
            return null;
        }

        var path = text.Trim();
        if (path.StartsWith("source.", StringComparison.Ordinal))
        {
            path = path[7..];
        }

        if (path.Length == 0 || path.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '"'))
        {
            error = "Seuls les chemins de champ sont pris en charge.";
            return null;
        }

        return new PathExpression(path);
    }

    private sealed class PathExpression(string path) : CompiledExpression
    {
        public override object? Evaluate(ErpConnect.Connectors.Sdk.Record source) => source.GetPath(path);
    }
}
