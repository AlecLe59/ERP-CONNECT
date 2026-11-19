using System.Collections;
using System.Globalization;

namespace ErpConnect.Connectors.Sdk;

/// <summary>
/// Format pivot : enregistrement typé, ordonné, éventuellement imbriqué.
/// Types admis : null, string, long, decimal, double, bool, DateTimeOffset, DateOnly, byte[], Record, IReadOnlyList&lt;object?&gt;.
/// Les autres types numériques sont normalisés à l'insertion (int → long, float → double).
/// </summary>
public sealed class Record : IEnumerable<KeyValuePair<string, object?>>
{
    /// <summary>Profondeur d'imbrication maximale (protège contre les fichiers malveillants).</summary>
    public const int MaxDepth = 32;

    private readonly List<string> _order = [];
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public int Count => _order.Count;

    public IEnumerable<string> Names => _order;

    public object? this[string name]
    {
        get => _values.TryGetValue(name, out var v) ? v : null;
        set => Set(name, value);
    }

    public bool Contains(string name) => _values.ContainsKey(name);

    public bool TryGet(string name, out object? value) => _values.TryGetValue(name, out value);

    public Record Set(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var normalized = Normalize(value, 0);
        if (!_values.ContainsKey(name))
        {
            _order.Add(name);
        }

        _values[name] = normalized;
        return this;
    }

    public bool Remove(string name)
    {
        if (!_values.Remove(name))
        {
            return false;
        }

        _order.Remove(name);
        return true;
    }

    /// <summary>Lit un chemin pointé (client.adresse.ville). Renvoie null si un segment manque.</summary>
    public object? GetPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        object? current = this;
        foreach (var segment in path.Split('.'))
        {
            current = current switch
            {
                Record r => r[segment],
                IReadOnlyList<object?> list when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < list.Count => list[i],
                _ => null,
            };
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        foreach (var name in _order)
        {
            yield return new KeyValuePair<string, object?>(name, _values[name]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static Record From(params (string Name, object? Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var r = new Record();
        foreach (var (name, value) in fields)
        {
            r.Set(name, value);
        }

        return r;
    }

    public static object? Normalize(object? value, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Imbrication supérieure à {MaxDepth} niveaux.");
        }

        return value switch
        {
            null => null,
            string or long or decimal or double or bool or DateTimeOffset or DateOnly or byte[] => value,
            Record r => CheckDepth(r, depth),
            int i => (long)i,
            short s => (long)s,
            byte b => (long)b,
            uint ui => (long)ui,
            float f => (double)f,
            DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
            Guid g => g.ToString(),
            IReadOnlyList<object?> list => list.Select(x => Normalize(x, depth + 1)).ToList(),
            IEnumerable e and not IDictionary => e.Cast<object?>().Select(x => Normalize(x, depth + 1)).ToList(),
            _ => throw new ConnectorException(ErrorKind.Permanent, $"Type non pris en charge dans un enregistrement : {value.GetType().Name}."),
        };
    }

    private static Record CheckDepth(Record r, int depth)
    {
        foreach (var (_, v) in r)
        {
            if (v is Record or IReadOnlyList<object?>)
            {
                Normalize(v is Record inner ? inner : v, depth + 1);
            }
        }

        return r;
    }

    /// <summary>Représentation texte invariante d'une valeur scalaire (écriture CSV, XML, clés).</summary>
    public static string? ToInvariantString(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}

/// <summary>Lot d'enregistrements lus, avec la position à partir de laquelle reprendre après ce lot.</summary>
public sealed record RecordBatch(IReadOnlyList<Record> Records, string? Cursor);
