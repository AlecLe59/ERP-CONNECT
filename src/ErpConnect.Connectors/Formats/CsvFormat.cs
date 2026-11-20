using System.Runtime.CompilerServices;
using System.Text;
using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors.Formats;

/// <summary>
/// CSV conforme RFC 4180, lu en flux (mémoire constante). Options : delimiter, encoding (utf-8, windows-1252), header.
/// Limites : 1 Mio par champ, 4096 colonnes (protection contre les fichiers malveillants).
/// </summary>
public sealed class CsvFormat : IRecordFormat
{
    public const int MaxFieldLength = 1024 * 1024;
    public const int MaxColumns = 4096;

    static CsvFormat() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Id => "csv";

    public IReadOnlyList<ConfigField> Options { get; } =
    [
        new("delimiter", "Séparateur", ConfigFieldType.Choice, Default: ";", Choices: [";", ",", "\t", "|"]),
        new("encoding", "Encodage", ConfigFieldType.Choice, Default: "utf-8", Choices: ["utf-8", "windows-1252"]),
        new("header", "Première ligne = en-têtes", ConfigFieldType.Boolean, Default: "true"),
    ];

    internal static Encoding ResolveEncoding(string? name) => name switch
    {
        null or "" or "utf-8" => new UTF8Encoding(false),
        "windows-1252" => Encoding.GetEncoding(1252),
        _ => throw new ConnectorException(ErrorKind.Permanent, $"Encodage non pris en charge : {name}."),
    };

    private static char Delimiter(IReadOnlyDictionary<string, string> o) =>
        o.TryGetValue("delimiter", out var d) && d.Length == 1 ? d[0] : ';';

    private static bool HasHeader(IReadOnlyDictionary<string, string> o) =>
        !o.TryGetValue("header", out var h) || !string.Equals(h, "false", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<Record> ReadAsync(Stream input, IReadOnlyDictionary<string, string> options, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        using var reader = new StreamReader(input, ResolveEncoding(options.GetValueOrDefault("encoding")), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var parser = new CsvParser(reader, Delimiter(options));
        string[]? headers = null;
        var line = 0L;
        while (await parser.ReadRowAsync(ct).ConfigureAwait(false) is { } row)
        {
            ct.ThrowIfCancellationRequested();
            line++;
            if (headers is null && HasHeader(options))
            {
                headers = MakeUnique(row);
                continue;
            }

            headers ??= Enumerable.Range(1, row.Count).Select(i => $"col{i}").ToArray();
            if (row.Count == 1 && row[0].Length == 0)
            {
                continue; // ligne vide
            }

            if (row.Count > headers.Length)
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Ligne {line} : {row.Count} colonnes au lieu de {headers.Length}.");
            }

            var record = new Record();
            for (var i = 0; i < headers.Length; i++)
            {
                record.Set(headers[i], i < row.Count ? row[i] : null);
            }

            yield return record;
        }
    }

    private static string[] MakeUnique(List<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var baseName = string.IsNullOrWhiteSpace(names[i]) ? $"col{i + 1}" : names[i].Trim();
            var name = baseName;
            for (var n = 2; !seen.Add(name); n++)
            {
                name = $"{baseName}_{n}";
            }

            result[i] = name;
        }

        return result;
    }

    public IFormatWriter CreateWriter(Stream output, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CsvWriter(output, Delimiter(options), ResolveEncoding(options.GetValueOrDefault("encoding")), HasHeader(options));
    }

    private sealed class CsvParser
    {
        private readonly TextReader _reader;
        private readonly char _delimiter;
        private readonly char[] _buffer = new char[64 * 1024];
        private int _pos;
        private int _len;

        public CsvParser(TextReader reader, char delimiter)
        {
            _reader = reader;
            _delimiter = delimiter;
        }

        private async ValueTask<int> PeekAsync(CancellationToken ct)
        {
            if (_pos >= _len)
            {
                _len = await _reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                _pos = 0;
                if (_len == 0)
                {
                    return -1;
                }
            }

            return _buffer[_pos];
        }

        public async ValueTask<List<string>?> ReadRowAsync(CancellationToken ct)
        {
            if (await PeekAsync(ct).ConfigureAwait(false) < 0)
            {
                return null;
            }

            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            var wasQuoted = false;
            while (true)
            {
                var c = await PeekAsync(ct).ConfigureAwait(false);
                if (c < 0)
                {
                    if (quoted)
                    {
                        throw new ConnectorException(ErrorKind.Permanent, "Guillemet non fermé en fin de fichier.");
                    }

                    row.Add(field.ToString());
                    return row;
                }

                _pos++;
                var ch = (char)c;
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (await PeekAsync(ct).ConfigureAwait(false) == '"')
                        {
                            _pos++;
                            field.Append('"');
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        field.Append(ch);
                    }
                }
                else if (ch == '"' && field.Length == 0 && !wasQuoted)
                {
                    quoted = true;
                    wasQuoted = true;
                }
                else if (ch == _delimiter)
                {
                    AddField(row, field);
                    wasQuoted = false;
                }
                else if (ch is '\r' or '\n')
                {
                    if (ch == '\r' && await PeekAsync(ct).ConfigureAwait(false) == '\n')
                    {
                        _pos++;
                    }

                    row.Add(field.ToString());
                    return row;
                }
                else
                {
                    field.Append(ch);
                }

                if (field.Length > MaxFieldLength)
                {
                    throw new ConnectorException(ErrorKind.Permanent, "Champ CSV supérieur à 1 Mio.");
                }
            }
        }

        private static void AddField(List<string> row, StringBuilder field)
        {
            row.Add(field.ToString());
            field.Clear();
            if (row.Count > MaxColumns)
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Plus de {MaxColumns} colonnes.");
            }
        }
    }

    private sealed class CsvWriter : IFormatWriter
    {
        private readonly StreamWriter _writer;
        private readonly char _delimiter;
        private readonly bool _header;
        private string[]? _columns;

        public CsvWriter(Stream output, char delimiter, Encoding encoding, bool header)
        {
            _writer = new StreamWriter(output, encoding, 64 * 1024, leaveOpen: true) { NewLine = "\r\n" };
            _delimiter = delimiter;
            _header = header;
        }

        public async Task WriteAsync(Record record, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (_columns is null)
            {
                _columns = record.Names.ToArray();
                if (_header)
                {
                    await WriteRowAsync(_columns, ct).ConfigureAwait(false);
                }
            }

            foreach (var name in record.Names)
            {
                if (!_columns.Contains(name, StringComparer.Ordinal))
                {
                    throw new ConnectorException(ErrorKind.Permanent, $"Colonne {name} absente du premier enregistrement : un CSV a des colonnes fixes.");
                }
            }

            await WriteRowAsync(_columns.Select(c => record[c] is Record or IReadOnlyList<object?>
                ? throw new ConnectorException(ErrorKind.Permanent, $"La colonne {c} contient une structure imbriquée : aplatissez-la dans le mapping.")
                : Record.ToInvariantString(record[c]) ?? string.Empty), ct).ConfigureAwait(false);
        }

        private async Task WriteRowAsync(IEnumerable<string> values, CancellationToken ct)
        {
            var first = true;
            foreach (var v in values)
            {
                if (!first)
                {
                    await _writer.WriteAsync(_delimiter).ConfigureAwait(false);
                }

                first = false;
                await _writer.WriteAsync(Escape(v).AsMemory(), ct).ConfigureAwait(false);
            }

            await _writer.WriteLineAsync().ConfigureAwait(false);
        }

        private string Escape(string v)
        {
            var needsQuote = v.Contains(_delimiter, StringComparison.Ordinal) || v.Contains('"', StringComparison.Ordinal) || v.Contains('\n', StringComparison.Ordinal) || v.Contains('\r', StringComparison.Ordinal);
            return needsQuote ? $"\"{v.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : v;
        }

        public async Task FinishAsync(CancellationToken ct) => await _writer.FlushAsync(ct).ConfigureAwait(false);

        public ValueTask DisposeAsync() => _writer.DisposeAsync();
    }
}
