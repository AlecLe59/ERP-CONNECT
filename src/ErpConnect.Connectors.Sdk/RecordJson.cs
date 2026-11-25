using System.Text.Json;

namespace ErpConnect.Connectors.Sdk;

/// <summary>Sérialisation JSON canonique d'un enregistrement (rejets, aperçu, clés de doublon, format JSON).</summary>
public static class RecordJson
{
    public static void WriteValue(Utf8JsonWriter w, object? value)
    {
        ArgumentNullException.ThrowIfNull(w);
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case long l: w.WriteNumberValue(l); break;
            case decimal d: w.WriteNumberValue(d); break;
            case double db when double.IsFinite(db): w.WriteNumberValue(db); break;
            case double: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case DateTimeOffset dto: w.WriteStringValue(dto); break;
            case DateOnly date: w.WriteStringValue(Record.ToInvariantString(date)); break;
            case byte[] bytes: w.WriteBase64StringValue(bytes); break;
            case Record r: WriteRecord(w, r); break;
            case IReadOnlyList<object?> list:
                w.WriteStartArray();
                foreach (var x in list)
                {
                    WriteValue(w, x);
                }

                w.WriteEndArray();
                break;
            default: w.WriteStringValue(Record.ToInvariantString(value)); break;
        }
    }

    public static void WriteRecord(Utf8JsonWriter w, Record r)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(r);
        w.WriteStartObject();
        foreach (var (name, value) in r)
        {
            w.WritePropertyName(name);
            WriteValue(w, value);
        }

        w.WriteEndObject();
    }

    public static string Serialize(Record r, int maxLength = int.MaxValue)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            WriteRecord(w, r);
        }

        var s = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        return s.Length > maxLength ? s[..maxLength] + "…" : s;
    }

    public static byte[] SerializeToUtf8(Record r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            WriteRecord(w, r);
        }

        return ms.ToArray();
    }
}
