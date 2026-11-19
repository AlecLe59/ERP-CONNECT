using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ErpConnect.Connectors.Sdk;

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

/// <summary>Expression compilée, évaluée sur un enregistrement source. Sans état : partageable entre lots.</summary>
public abstract class CompiledExpression
{
    public abstract object? Evaluate(Record source);

    /// <summary>Champs source lus par l'expression (pour l'éditeur visuel).</summary>
    public IReadOnlyList<string> Fields { get; internal set; } = [];
}

/// <summary>
/// Langage d'expressions de correspondance (Phase 4). Pas de boucle, pas d'accès système, pas de réflexion :
/// le temps d'évaluation est borné par la taille de l'expression. Voir docs/architecture/expressions.md.
/// </summary>
public static class ExpressionCompiler
{
    public const int MaxLength = 4096;
    public const int MaxDepth = 64;
    public const int MaxStringLength = 1024 * 1024;

    /// <summary>Fonctions disponibles (nom, signature, aide) pour l'éditeur.</summary>
    public static IReadOnlyList<(string Name, string Signature, string Help)> FunctionCatalog { get; } = Functions.Catalog.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static CompiledExpression? TryCompile(string? text, out string? error)
    {
        error = null;
        try
        {
            return Compile(text);
        }
        catch (ExpressionException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static CompiledExpression Compile(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ExpressionException("Expression vide.");
        }

        if (text.Length > MaxLength)
        {
            throw new ExpressionException($"Expression de plus de {MaxLength} caractères.");
        }

        var parser = new Parser(Lexer.Tokenize(text));
        var node = parser.ParseExpression(0);
        parser.ExpectEnd();
        var fields = new SortedSet<string>(StringComparer.Ordinal);
        node.CollectFields(fields);
        return new Compiled(node) { Fields = fields.ToArray() };
    }

    private sealed class Compiled(Node root) : CompiledExpression
    {
        public override object? Evaluate(Record source) => root.Eval(source);
    }

    // ---------------------------------------------------------------- lexique

    internal enum T
    {
        Number,
        Text,
        Ident,
        Field,
        Op,
        LParen,
        RParen,
        Comma,
        End,
    }

    internal readonly record struct Token(T Kind, string Value, int Position);

    private static class Lexer
    {
        private static readonly string[] Ops = ["<=", ">=", "!=", "<>", "==", "=", "<", ">", "+", "-", "*", "/", "%", "&", "."];

        public static List<Token> Tokenize(string s)
        {
            var list = new List<Token>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (char.IsAsciiDigit(c))
                {
                    var start = i;
                    while (i < s.Length && (char.IsAsciiDigit(s[i]) || (s[i] == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))))
                    {
                        i++;
                    }

                    list.Add(new Token(T.Number, s[start..i], start));
                }
                else if (c is '\'' or '"')
                {
                    var start = i++;
                    var sb = new StringBuilder();
                    while (true)
                    {
                        if (i >= s.Length)
                        {
                            throw new ExpressionException($"Texte non fermé (position {start + 1}).");
                        }

                        if (s[i] == c)
                        {
                            if (i + 1 < s.Length && s[i + 1] == c)
                            {
                                sb.Append(c);
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        sb.Append(s[i++]);
                    }

                    list.Add(new Token(T.Text, sb.ToString(), start));
                }
                else if (c == '[')
                {
                    var end = s.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        throw new ExpressionException($"Crochet non fermé (position {i + 1}).");
                    }

                    list.Add(new Token(T.Field, s[(i + 1)..end], i));
                    i = end + 1;
                }
                else if (char.IsLetter(c) || c is '_' or '@' or '#')
                {
                    var start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '@' or '#'))
                    {
                        i++;
                    }

                    list.Add(new Token(T.Ident, s[start..i], start));
                }
                else if (c == '(')
                {
                    list.Add(new Token(T.LParen, "(", i++));
                }
                else if (c == ')')
                {
                    list.Add(new Token(T.RParen, ")", i++));
                }
                else if (c is ',' or ';')
                {
                    list.Add(new Token(T.Comma, ",", i++));
                }
                else
                {
                    var op = Ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0)
                        ?? throw new ExpressionException($"Caractère inattendu « {c} » (position {i + 1}).");
                    list.Add(new Token(T.Op, op, i));
                    i += op.Length;
                }
            }

            list.Add(new Token(T.End, string.Empty, s.Length));
            return list;
        }
    }

    // ---------------------------------------------------------------- syntaxe

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;
        private int _depth;

        private Token Peek => tokens[_pos];

        private Token Next() => tokens[_pos++];

        private bool IsKeyword(string k) => Peek.Kind == T.Ident && string.Equals(Peek.Value, k, StringComparison.OrdinalIgnoreCase);

        private bool IsOp(string op) => Peek.Kind == T.Op && Peek.Value == op;

        public void ExpectEnd()
        {
            if (Peek.Kind != T.End)
            {
                throw new ExpressionException($"Élément inattendu « {Peek.Value} » (position {Peek.Position + 1}).");
            }
        }

        private static int Precedence(string op) => op switch
        {
            "or" => 1,
            "and" => 2,
            "=" or "==" or "!=" or "<>" or "<" or "<=" or ">" or ">=" => 4,
            "&" => 5,
            "+" or "-" => 6,
            "*" or "/" or "%" => 7,
            _ => -1,
        };

        private string? BinaryOp()
        {
            if (Peek.Kind == T.Op && Peek.Value != ".")
            {
                return Peek.Value;
            }

            return IsKeyword("and") ? "and" : IsKeyword("or") ? "or" : null;
        }

        public Node ParseExpression(int minPrecedence)
        {
            if (++_depth > MaxDepth)
            {
                throw new ExpressionException($"Expression imbriquée sur plus de {MaxDepth} niveaux.");
            }

            var left = ParseUnary();
            while (BinaryOp() is { } op && Precedence(op) >= minPrecedence && Precedence(op) > 0)
            {
                Next();
                var right = ParseExpression(Precedence(op) + 1);
                left = new Binary(op, left, right);
            }

            _depth--;
            return left;
        }

        private Node ParseUnary()
        {
            if (IsKeyword("not"))
            {
                Next();
                return new Not(ParseExpression(3));
            }

            if (IsOp("-"))
            {
                Next();
                return new Binary("-", new Literal(0L), ParseUnary());
            }

            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var t = Next();
            switch (t.Kind)
            {
                case T.Number:
                    return new Literal(t.Value.Contains('.', StringComparison.Ordinal)
                        ? decimal.Parse(t.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
                        : long.TryParse(t.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : decimal.Parse(t.Value, CultureInfo.InvariantCulture));
                case T.Text:
                    return new Literal(t.Value);
                case T.LParen:
                    var inner = ParseExpression(0);
                    Expect(T.RParen, ")");
                    return inner;
                case T.Field:
                    return ParsePath(t.Value);
                case T.Ident:
                    switch (t.Value.ToLowerInvariant())
                    {
                        case "true": return new Literal(true);
                        case "false": return new Literal(false);
                        case "null": return new Literal(null);
                    }

                    if (Peek.Kind == T.LParen)
                    {
                        Next();
                        var args = new List<Node>();
                        if (Peek.Kind != T.RParen)
                        {
                            do
                            {
                                args.Add(ParseExpression(0));
                            }
                            while (Peek.Kind == T.Comma && Next().Kind == T.Comma);
                        }

                        Expect(T.RParen, ")");
                        return Functions.Create(t.Value, args, t.Position);
                    }

                    return ParsePath(string.Equals(t.Value, "source", StringComparison.Ordinal) && IsOp(".") ? null : t.Value);
                default:
                    throw new ExpressionException(t.Kind == T.End ? "Expression incomplète." : $"Élément inattendu « {t.Value} » (position {t.Position + 1}).");
            }
        }

        private Path ParsePath(string? first)
        {
            var segments = new List<string>();
            if (first is not null)
            {
                segments.Add(first);
            }

            while (IsOp("."))
            {
                Next();
                var s = Next();
                if (s.Kind is not (T.Ident or T.Field or T.Number))
                {
                    throw new ExpressionException($"Nom de champ attendu après « . » (position {s.Position + 1}).");
                }

                segments.Add(s.Value);
            }

            if (segments.Count == 0)
            {
                throw new ExpressionException("Nom de champ attendu après « source ».");
            }

            return new Path(segments.ToArray());
        }

        private void Expect(T kind, string text)
        {
            if (Next().Kind != kind)
            {
                throw new ExpressionException($"« {text} » attendu (position {tokens[_pos - 1].Position + 1}).");
            }
        }
    }

    // ---------------------------------------------------------------- arbre

    internal abstract class Node
    {
        public abstract object? Eval(Record r);

        public virtual void CollectFields(ISet<string> fields)
        {
        }
    }

    private sealed class Literal(object? value) : Node
    {
        public override object? Eval(Record r) => value;
    }

    private sealed class Path(string[] segments) : Node
    {
        private readonly string _joined = string.Join('.', segments);

        public override object? Eval(Record r)
        {
            object? current = r;
            foreach (var s in segments)
            {
                current = current switch
                {
                    Record rec => rec[s],
                    IReadOnlyList<object?> list when int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var i) => i < list.Count ? list[i] : null,
                    _ => null,
                };
                if (current is null)
                {
                    return null;
                }
            }

            return current;
        }

        public override void CollectFields(ISet<string> fields) => fields.Add(_joined);
    }

    private sealed class Not(Node operand) : Node
    {
        public override object? Eval(Record r) => !Values.ToBool(operand.Eval(r));

        public override void CollectFields(ISet<string> fields) => operand.CollectFields(fields);
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override object? Eval(Record r)
        {
            switch (op)
            {
                case "and":
                    return Values.ToBool(left.Eval(r)) && Values.ToBool(right.Eval(r));
                case "or":
                    return Values.ToBool(left.Eval(r)) || Values.ToBool(right.Eval(r));
            }

            var a = left.Eval(r);
            var b = right.Eval(r);
            return op switch
            {
                "&" => Values.Limit(Values.Text(a) + Values.Text(b)),
                "=" or "==" => Values.AreEqual(a, b),
                "!=" or "<>" => !Values.AreEqual(a, b),
                "<" => Values.Compare(a, b) < 0,
                "<=" => Values.Compare(a, b) <= 0,
                ">" => Values.Compare(a, b) > 0,
                ">=" => Values.Compare(a, b) >= 0,
                _ => Values.Arithmetic(op, a, b),
            };
        }

        public override void CollectFields(ISet<string> fields)
        {
            left.CollectFields(fields);
            right.CollectFields(fields);
        }
    }

    internal sealed class Call(string name, Node[] args, Func<Record, Node[], object?> impl) : Node
    {
        public string Name { get; } = name;

        public override object? Eval(Record r) => impl(r, args);

        public override void CollectFields(ISet<string> fields)
        {
            foreach (var a in args)
            {
                a.CollectFields(fields);
            }
        }
    }

    // ---------------------------------------------------------------- valeurs

    internal static class Values
    {
        public static string Limit(string s) => s.Length > MaxStringLength ? throw new ExpressionException("Texte résultant supérieur à 1 Mio.") : s;

        public static string Text(object? v) => v switch
        {
            null => string.Empty,
            Record or IReadOnlyList<object?> => throw new ExpressionException("Une structure ne peut pas être convertie en texte."),
            _ => Record.ToInvariantString(v) ?? string.Empty,
        };

        public static bool ToBool(object? v) => v switch
        {
            bool b => b,
            null => false,
            string s when string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1" || string.Equals(s, "oui", StringComparison.OrdinalIgnoreCase) => true,
            string s when string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || s == "0" || string.Equals(s, "non", StringComparison.OrdinalIgnoreCase) || s.Length == 0 => false,
            long l => l != 0,
            _ => throw new ExpressionException($"Valeur booléenne attendue, reçu « {Display(v)} »."),
        };

        public static string Display(object? v) => v is null ? "null" : v is Record ? "{…}" : v is IReadOnlyList<object?> ? "[…]" : Truncate(Text(v));

        private static string Truncate(string s) => s.Length > 40 ? s[..40] + "…" : s;

        /// <summary>Conversion numérique : texte au format invariant (point décimal), espaces tolérés.</summary>
        public static object? Number(object? v, string? decimalSeparator = null) => v switch
        {
            null => null,
            long or decimal or double => v,
            bool b => b ? 1L : 0L,
            string s when s.Trim().Length == 0 => null,
            string s => ParseNumber(s, decimalSeparator),
            _ => throw new ExpressionException($"« {Display(v)} » n'est pas un nombre."),
        };

        private static object ParseNumber(string s, string? sep)
        {
            var t = s.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
            if (sep == ",")
            {
                t = t.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
            }

            if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            {
                return l;
            }

            if (decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d))
            {
                return d;
            }

            throw new ExpressionException($"« {Truncate(s)} » n'est pas un nombre.");
        }

        public static decimal Dec(object? v) => Number(v) switch
        {
            null => throw new ExpressionException("Nombre attendu, valeur vide."),
            long l => l,
            decimal d => d,
            double db => double.IsFinite(db) ? (decimal)db : throw new ExpressionException("Nombre non fini."),
            _ => throw new ExpressionException("Nombre attendu."),
        };

        public static object? Arithmetic(string op, object? a, object? b)
        {
            if (a is null || b is null)
            {
                return null;
            }

            var x = Number(a);
            var y = Number(b);
            if (x is null || y is null)
            {
                return null;
            }

            try
            {
                if (x is long lx && y is long ly && op != "/")
                {
                    return op switch
                    {
                        "+" => checked(lx + ly),
                        "-" => checked(lx - ly),
                        "*" => checked(lx * ly),
                        "%" => ly == 0 ? throw new ExpressionException("Division par zéro.") : lx % ly,
                        _ => throw new ExpressionException($"Opérateur inconnu {op}."),
                    };
                }

                var dx = Dec(x);
                var dy = Dec(y);
                return op switch
                {
                    "+" => dx + dy,
                    "-" => dx - dy,
                    "*" => dx * dy,
                    "/" => dy == 0 ? throw new ExpressionException("Division par zéro.") : Normalize(dx / dy),
                    "%" => dy == 0 ? throw new ExpressionException("Division par zéro.") : dx % dy,
                    _ => throw new ExpressionException($"Opérateur inconnu {op}."),
                };
            }
            catch (OverflowException ex)
            {
                throw new ExpressionException("Dépassement de capacité numérique.", ex);
            }
        }

#pragma warning disable CA1859 // Le type de retour mixte (long ou decimal) est voulu.
        private static object Normalize(decimal d) => decimal.Truncate(d) == d && d is >= long.MinValue and <= long.MaxValue ? (long)d : (object)d;
#pragma warning restore CA1859

        public static bool AreEqual(object? a, object? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }

            if (TryNumbers(a, b, out var x, out var y))
            {
                return x == y;
            }

            if (a is DateTimeOffset or DateOnly || b is DateTimeOffset or DateOnly)
            {
                return Compare(a, b) == 0;
            }

            return string.Equals(Text(a), Text(b), StringComparison.Ordinal);
        }

        public static int Compare(object? a, object? b)
        {
            if (a is null || b is null)
            {
                throw new ExpressionException("Comparaison avec une valeur vide : utilisez isNull().");
            }

            if (TryNumbers(a, b, out var x, out var y))
            {
                return x.CompareTo(y);
            }

            if (a is DateTimeOffset or DateOnly || b is DateTimeOffset or DateOnly)
            {
                return ToDateTime(a, null).CompareTo(ToDateTime(b, null));
            }

            return string.CompareOrdinal(Text(a), Text(b));
        }

        private static bool TryNumbers(object a, object b, out decimal x, out decimal y)
        {
            x = y = 0;
            if (a is bool || b is bool || a is DateTimeOffset or DateOnly || b is DateTimeOffset or DateOnly)
            {
                return false;
            }

            var na = a is string sa ? TryParse(sa) : a is long or decimal or double ? a : null;
            var nb = b is string sb ? TryParse(sb) : b is long or decimal or double ? b : null;
            if (na is null || nb is null || (a is string && b is string))
            {
                return false;
            }

            x = Dec(na);
            y = Dec(nb);
            return true;
        }

        private static object? TryParse(string s)
        {
            try
            {
                return s.Trim().Length == 0 ? null : Number(s);
            }
            catch (ExpressionException)
            {
                return null;
            }
        }

        public static DateTimeOffset ToDateTime(object? v, string? format) => v switch
        {
            DateTimeOffset d => d,
            DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            string s when format is not null && DateTimeOffset.TryParseExact(s.Trim(), format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) => d,
            string s when format is null && DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) => d,
            null => throw new ExpressionException("Date attendue, valeur vide."),
            _ => throw new ExpressionException($"« {Display(v)} » n'est pas une date{(format is null ? string.Empty : $" au format {format}")}."),
        };
    }
}
