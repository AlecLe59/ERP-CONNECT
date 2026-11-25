using System.Globalization;
using System.Text.RegularExpressions;
using ErpConnect.Connectors.Sdk;
using static ErpConnect.Flows.ExpressionCompiler;

namespace ErpConnect.Flows;

/// <summary>Bibliothèque de fonctions du langage d'expressions. Noms insensibles à la casse.</summary>
internal static class Functions
{
    private sealed record Def(int Min, int Max, Func<Record, Node[], object?> Impl, string Signature, string Help);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static object? A(Record r, Node[] a, int i) => a[i].Eval(r);

    private static string S(Record r, Node[] a, int i) => Values.Text(A(r, a, i));

    private static int I(Record r, Node[] a, int i)
    {
        var d = Values.Dec(A(r, a, i));
        return d is < int.MinValue or > int.MaxValue ? throw new ExpressionException("Entier hors limites.") : (int)decimal.Truncate(d);
    }

    private static readonly Dictionary<string, Def> Library = new(StringComparer.OrdinalIgnoreCase)
    {
        // Texte
        ["upper"] = new(1, 1, (r, a) => A(r, a, 0) is null ? null : S(r, a, 0).ToUpperInvariant(), "upper(texte)", "Majuscules"),
        ["lower"] = new(1, 1, (r, a) => A(r, a, 0) is null ? null : S(r, a, 0).ToLowerInvariant(), "lower(texte)", "Minuscules"),
        ["trim"] = new(1, 1, (r, a) => A(r, a, 0) is null ? null : S(r, a, 0).Trim(), "trim(texte)", "Supprime les espaces en début et fin"),
        ["len"] = new(1, 1, (r, a) => (long)S(r, a, 0).Length, "len(texte)", "Nombre de caractères"),
        ["left"] = new(2, 2, (r, a) => { var s = S(r, a, 0); var n = Math.Clamp(I(r, a, 1), 0, s.Length); return s[..n]; }, "left(texte, n)", "n premiers caractères"),
        ["right"] = new(2, 2, (r, a) => { var s = S(r, a, 0); var n = Math.Clamp(I(r, a, 1), 0, s.Length); return s[^n..]; }, "right(texte, n)", "n derniers caractères"),
        ["substr"] = new(2, 3, Substr, "substr(texte, début, longueur?)", "Extrait à partir de la position début (1 = premier caractère)"),
        ["replace"] = new(3, 3, (r, a) => Values.Limit(S(r, a, 0).Replace(S(r, a, 1).Length == 0 ? throw new ExpressionException("replace : texte recherché vide.") : S(r, a, 1), S(r, a, 2), StringComparison.Ordinal)), "replace(texte, ancien, nouveau)", "Remplace toutes les occurrences"),
        ["contains"] = new(2, 2, (r, a) => S(r, a, 0).Contains(S(r, a, 1), StringComparison.Ordinal), "contains(texte, partie)", "Vrai si le texte contient la partie"),
        ["startsWith"] = new(2, 2, (r, a) => S(r, a, 0).StartsWith(S(r, a, 1), StringComparison.Ordinal), "startsWith(texte, début)", ""),
        ["endsWith"] = new(2, 2, (r, a) => S(r, a, 0).EndsWith(S(r, a, 1), StringComparison.Ordinal), "endsWith(texte, fin)", ""),
        ["padLeft"] = new(2, 3, (r, a) => Values.Limit(S(r, a, 0).PadLeft(Math.Clamp(I(r, a, 1), 0, MaxStringLength), a.Length > 2 ? Char(S(r, a, 2)) : ' ')), "padLeft(texte, longueur, caractère?)", "Complète à gauche (ex. zéros)"),
        ["padRight"] = new(2, 3, (r, a) => Values.Limit(S(r, a, 0).PadRight(Math.Clamp(I(r, a, 1), 0, MaxStringLength), a.Length > 2 ? Char(S(r, a, 2)) : ' ')), "padRight(texte, longueur, caractère?)", ""),
        ["concat"] = new(1, 32, (r, a) => Values.Limit(string.Concat(a.Select(x => Values.Text(x.Eval(r))))), "concat(a, b, …)", "Concatène (vide si null)"),
        ["split"] = new(3, 3, (r, a) => { var parts = S(r, a, 0).Split(S(r, a, 1)); var i = I(r, a, 2); return i >= 1 && i <= parts.Length ? parts[i - 1] : null; }, "split(texte, séparateur, rang)", "Rang-ième morceau (1 = premier)"),
        ["regexMatch"] = new(2, 2, (r, a) => SafeRegex(() => Regex.IsMatch(S(r, a, 0), S(r, a, 1), RegexOptions.CultureInvariant, RegexTimeout)), "regexMatch(texte, motif)", "Expression régulière (100 ms max)"),
        ["regexReplace"] = new(3, 3, (r, a) => SafeRegex(() => Values.Limit(Regex.Replace(S(r, a, 0), S(r, a, 1), S(r, a, 2), RegexOptions.CultureInvariant, RegexTimeout))), "regexReplace(texte, motif, remplacement)", ""),

        // Valeurs vides et conditions (évaluation paresseuse)
        ["coalesce"] = new(1, 32, (r, a) => a.Select(x => x.Eval(r)).FirstOrDefault(v => v is not null and not ""), "coalesce(a, b, …)", "Première valeur non vide"),
        ["isNull"] = new(1, 1, (r, a) => A(r, a, 0) is null, "isNull(valeur)", ""),
        ["isEmpty"] = new(1, 1, (r, a) => A(r, a, 0) is null or "", "isEmpty(valeur)", "Vrai si null ou texte vide"),
        ["if"] = new(3, 3, (r, a) => Values.ToBool(A(r, a, 0)) ? A(r, a, 1) : A(r, a, 2), "if(condition, siVrai, siFaux)", ""),
        ["switch"] = new(3, 63, Switch, "switch(valeur, cas1, résultat1, …, défaut?)", "Table de correspondance"),
        ["error"] = new(1, 1, (r, a) => throw new ExpressionException(S(r, a, 0)), "error(message)", "Rejette l'enregistrement avec ce motif"),

        // Nombres
        ["toNumber"] = new(1, 2, (r, a) => Values.Number(A(r, a, 0), a.Length > 1 ? S(r, a, 1) : null), "toNumber(texte, séparateurDécimal?)", "Texte → nombre (séparateur « , » pour 1.234,56)"),
        ["toInteger"] = new(1, 1, (r, a) => A(r, a, 0) is null ? null : (object)checked((long)decimal.Truncate(Values.Dec(A(r, a, 0)))), "toInteger(valeur)", "Partie entière"),
        ["round"] = new(1, 2, (r, a) => A(r, a, 0) is null ? null : Math.Round(Values.Dec(A(r, a, 0)), a.Length > 1 ? Math.Clamp(I(r, a, 1), 0, 28) : 0, MidpointRounding.AwayFromZero), "round(nombre, décimales?)", "Arrondi commercial"),
        ["floor"] = new(1, 1, (r, a) => A(r, a, 0) is null ? null : decimal.Floor(Values.Dec(A(r, a, 0))), "floor(nombre)", ""),
        ["ceil"] = new(1, 1, (r, a) => A(r, a, 0) is null ? null : decimal.Ceiling(Values.Dec(A(r, a, 0))), "ceil(nombre)", ""),
        ["abs"] = new(1, 1, (r, a) => A(r, a, 0) is null ? null : Math.Abs(Values.Dec(A(r, a, 0))), "abs(nombre)", ""),
        ["min"] = new(2, 2, (r, a) => Math.Min(Values.Dec(A(r, a, 0)), Values.Dec(A(r, a, 1))), "min(a, b)", ""),
        ["max"] = new(2, 2, (r, a) => Math.Max(Values.Dec(A(r, a, 0)), Values.Dec(A(r, a, 1))), "max(a, b)", ""),
        ["toText"] = new(1, 2, ToText, "toText(valeur, format?)", "Valeur → texte (format .NET invariant, ex. 0.00)"),
        ["toBool"] = new(1, 1, (r, a) => Values.ToBool(A(r, a, 0)), "toBool(valeur)", "true/false, 1/0, oui/non"),

        // Dates
        ["toDate"] = new(1, 2, (r, a) => A(r, a, 0) is null or "" ? null : Values.ToDateTime(A(r, a, 0), a.Length > 1 ? S(r, a, 1) : null), "toDate(texte, format?)", "Texte → date (ex. dd/MM/yyyy)"),
        ["formatDate"] = new(2, 2, (r, a) => A(r, a, 0) is null ? null : Values.ToDateTime(A(r, a, 0), null).ToString(S(r, a, 1), CultureInfo.InvariantCulture), "formatDate(date, format)", "Date → texte (ex. yyyy-MM-dd)"),
        ["addDays"] = new(2, 2, (r, a) => Values.ToDateTime(A(r, a, 0), null).AddDays((double)Values.Dec(A(r, a, 1))), "addDays(date, n)", ""),
        ["now"] = new(0, 0, (_, _) => DateTimeOffset.UtcNow, "now()", "Date et heure UTC"),
        ["today"] = new(0, 0, (_, _) => DateOnly.FromDateTime(DateTime.UtcNow), "today()", "Date du jour (UTC)"),
    };

    public static IEnumerable<(string Name, string Signature, string Help)> Catalog => Library.Select(kv => (kv.Key, kv.Value.Signature, kv.Value.Help));

    public static Node Create(string name, List<Node> args, int position)
    {
        if (!Library.TryGetValue(name, out var def))
        {
            throw new ExpressionException($"Fonction inconnue « {name} » (position {position + 1}).");
        }

        if (args.Count < def.Min || args.Count > def.Max)
        {
            throw new ExpressionException($"{def.Signature} : {args.Count} argument(s) reçu(s).");
        }

        return new Call(name, args.ToArray(), (r, a) =>
        {
            try
            {
                return def.Impl(r, a);
            }
            catch (ConnectorException ex)
            {
                throw new ExpressionException(ex.Message, ex);
            }
            catch (FormatException ex)
            {
                throw new ExpressionException($"{name} : format invalide.", ex);
            }
            catch (OverflowException ex)
            {
                throw new ExpressionException($"{name} : dépassement de capacité.", ex);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new ExpressionException($"{name} : argument hors limites.", ex);
            }
        });
    }

    private static char Char(string s) => s.Length == 1 ? s[0] : throw new ExpressionException("Un seul caractère attendu.");

    private static string Substr(Record r, Node[] a)
    {
        var s = S(r, a, 0);
        var start = Math.Max(1, I(r, a, 1)) - 1;
        if (start >= s.Length)
        {
            return string.Empty;
        }

        var len = a.Length > 2 ? Math.Clamp(I(r, a, 2), 0, s.Length - start) : s.Length - start;
        return s.Substring(start, len);
    }

    private static object? Switch(Record r, Node[] a)
    {
        var v = A(r, a, 0);
        var i = 1;
        for (; i + 1 < a.Length; i += 2)
        {
            if (Values.AreEqual(v, a[i].Eval(r)))
            {
                return a[i + 1].Eval(r);
            }
        }

        return i < a.Length ? a[i].Eval(r) : null;
    }

    private static string? ToText(Record r, Node[] a)
    {
        var v = A(r, a, 0);
        if (v is null || a.Length == 1)
        {
            return v is null ? null : Values.Text(v);
        }

        var format = S(r, a, 1);
        return v switch
        {
            DateTimeOffset d => d.ToString(format, CultureInfo.InvariantCulture),
            _ => Values.Dec(v).ToString(format, CultureInfo.InvariantCulture),
        };
    }

    private static object SafeRegex(Func<object> f)
    {
        try
        {
            return f();
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new ExpressionException("Expression régulière trop coûteuse (plus de 100 ms).", ex);
        }
        catch (ArgumentException ex)
        {
            throw new ExpressionException($"Expression régulière invalide : {ex.Message}", ex);
        }
    }
}
