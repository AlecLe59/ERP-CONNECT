using System.Runtime.CompilerServices;
using System.Text.Json;
using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors.Formats;

/// <summary>JSON en flux : tableau d'objets à la racine (layout=array) ou un objet par ligne (layout=lines). Profondeur maximale 64.</summary>
public sealed class JsonFormat : IRecordFormat
{
    internal static readonly JsonSerializerOptions ReadOptions = new() { MaxDepth = 64, AllowTrailingCommas = false };

    public string Id => "json";

    public IReadOnlyList<ConfigField> Options { get; } =
    [
        new("layout", "Disposition", ConfigFieldType.Choice, Default: "array", Choices: ["array", "lines"]),
    ];

    private static bool Lines(IReadOnlyDictionary<string, string> o) => o.TryGetValue("layout", out var l) && l == "lines";

    public async IAsyncEnumerable<Record> ReadAsync(Stream input, IReadOnlyDictionary<string, string> options, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        if (Lines(options))
        {
            using var reader = new StreamReader(input, leaveOpen: true);
            var n = 0L;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                n++;
                ct.ThrowIfCancellationRequested();
                if (line.Length == 0)
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                }
                catch (JsonException ex)
                {
                    throw new ConnectorException(ErrorKind.Permanent, $"Ligne {n} : JSON invalide.", ex);
                }

                using (doc)
                {
                    yield return ToRecord(doc.RootElement, $"ligne {n}");
                }
            }

            yield break;
        }

        var items = JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(input, ReadOptions, ct);
        var enumerator = items.GetAsyncEnumerator(ct);
        await using (enumerator.ConfigureAwait(false))
        {
            var index = 0L;
            while (true)
            {
                bool has;
                try
                {
                    has = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    throw new ConnectorException(ErrorKind.Permanent, $"JSON invalide après l'élément {index} : {ex.Message}", ex);
                }

                if (!has)
                {
                    yield break;
                }

            ct.ThrowIfCancellationRequested();
                index++;
                yield return ToRecord(enumerator.Current, $"élément {index}");
            }
        }
    }

    internal static Record ToRecord(JsonElement e, string where)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new ConnectorException(ErrorKind.Permanent, $"{where} : objet JSON attendu.");
        }

        return (Record)Convert(e)!;
    }

    internal static object? Convert(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => ObjectToRecord(e),
        JsonValueKind.Array => e.EnumerateArray().Select(Convert).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.TryGetDecimal(out var d) ? d : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Record ObjectToRecord(JsonElement e)
    {
        var r = new Record();
        foreach (var p in e.EnumerateObject())
        {
            r.Set(p.Name, Convert(p.Value));
        }

        return r;
    }

    internal static void WriteValue(Utf8JsonWriter w, object? value) => RecordJson.WriteValue(w, value);

    internal static void WriteRecord(Utf8JsonWriter w, Record r) => RecordJson.WriteRecord(w, r);

    public IFormatWriter CreateWriter(Stream output, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Writer(output, Lines(options));
    }

    private sealed class Writer : IFormatWriter
    {
        private readonly Stream _output;
        private readonly bool _lines;
        private readonly Utf8JsonWriter _json;
        private bool _started;

        public Writer(Stream output, bool lines)
        {
            _output = output;
            _lines = lines;
            _json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false, SkipValidation = lines });
        }

        public async Task WriteAsync(Record record, CancellationToken ct)
        {
            if (!_started && !_lines)
            {
                _json.WriteStartArray();
            }

            _started = true;
            WriteRecord(_json, record);
            if (_lines)
            {
                await _json.FlushAsync(ct).ConfigureAwait(false);
                _output.WriteByte((byte)'\n');
                _json.Reset();
            }
            else if (_json.BytesPending > 64 * 1024)
            {
                await _json.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        public async Task FinishAsync(CancellationToken ct)
        {
            if (!_lines)
            {
                if (!_started)
                {
                    _json.WriteStartArray();
                }

                _json.WriteEndArray();
            }

            await _json.FlushAsync(ct).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _json.DisposeAsync();
    }
}
