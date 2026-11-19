using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ErpConnect.Connectors.Sdk;
using MiniExcelLibs;

namespace ErpConnect.Connectors.Formats;

/// <summary>
/// XLSX via MiniExcel (lecture en flux). Avant lecture, l'archive est contrôlée : taille décompressée totale
/// plafonnée et rapport de compression borné (protection « bombe zip »).
/// L'écriture passe par un tampon disque (JSON lignes) : la mémoire reste constante quel que soit le volume.
/// </summary>
public sealed class XlsxFormat : IRecordFormat
{
    public const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxCompressionRatio = 200;

    public string Id => "xlsx";

    public IReadOnlyList<ConfigField> Options { get; } =
    [
        new("sheet", "Feuille", ConfigFieldType.Text, Help: "Vide = première feuille"),
        new("header", "Première ligne = en-têtes", ConfigFieldType.Boolean, Default: "true"),
    ];

    public async IAsyncEnumerable<Record> ReadAsync(Stream input, IReadOnlyDictionary<string, string> options, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        string? tempPath = null;
        var stream = input;
        if (!input.CanSeek)
        {
            tempPath = Path.GetTempFileName();
            stream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            await input.CopyToAsync(stream, ct).ConfigureAwait(false);
            stream.Position = 0;
        }

        try
        {
            CheckArchive(stream);
            var header = !string.Equals(options.GetValueOrDefault("header"), "false", StringComparison.OrdinalIgnoreCase);
            var sheet = options.GetValueOrDefault("sheet") is { Length: > 0 } s ? s : null;
            IEnumerable<dynamic> rows;
            try
            {
                rows = MiniExcel.Query(stream, useHeaderRow: header, sheetName: sheet, excelType: ExcelType.XLSX);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Fichier XLSX illisible : {ex.Message}", ex);
            }

            foreach (IDictionary<string, object?> row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var r = new Record();
                var empty = true;
                foreach (var (k, v) in row)
                {
                    var value = ConvertCell(v);
                    empty &= value is null || (value is string str && str.Length == 0);
                    r.Set(string.IsNullOrWhiteSpace(k) ? $"col{r.Count + 1}" : k, value);
                }

                if (!empty)
                {
                    yield return r;
                }
            }
        }
        finally
        {
            if (tempPath is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static void CheckArchive(Stream stream)
    {
        var start = stream.Position;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            long total = 0;
            foreach (var e in zip.Entries)
            {
                total += e.Length;
                if (e.CompressedLength > 0 && e.Length / e.CompressedLength > MaxCompressionRatio)
                {
                    throw new ConnectorException(ErrorKind.Permanent, $"Fichier XLSX refusé : taux de compression anormal ({e.FullName}).");
                }
            }

            if (total > MaxUncompressedBytes)
            {
                throw new ConnectorException(ErrorKind.Permanent, "Fichier XLSX refusé : taille décompressée supérieure à 2 Gio.");
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ConnectorException(ErrorKind.Permanent, "Fichier XLSX invalide (archive illisible).", ex);
        }

        stream.Position = start;
    }

    private static object? ConvertCell(object? v) => v switch
    {
        null => null,
        double d when Math.Abs(d) < 9e15 && Math.Floor(d) == d => (long)d,
        double d => (decimal)d,
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        string or bool or long or decimal => v,
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    public IFormatWriter CreateWriter(Stream output, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Writer(output, options.GetValueOrDefault("sheet") is { Length: > 0 } s ? s : "Feuil1");
    }

    private sealed class Writer : IFormatWriter
    {
        private readonly Stream _output;
        private readonly string _sheet;
        private readonly string _bufferPath = Path.GetTempFileName();
        private readonly List<string> _columns = [];
        private readonly HashSet<string> _known = new(StringComparer.Ordinal);
        private StreamWriter? _buffer;

        public Writer(Stream output, string sheet)
        {
            _output = output;
            _sheet = sheet;
        }

        public async Task WriteAsync(Record record, CancellationToken ct)
        {
            _buffer ??= new StreamWriter(_bufferPath, append: false);
            foreach (var n in record.Names)
            {
                if (_known.Add(n))
                {
                    _columns.Add(n);
                }
            }

            using var ms = new MemoryStream();
            await using (var w = new Utf8JsonWriter(ms))
            {
                JsonFormat.WriteRecord(w, record);
            }

            await _buffer.WriteLineAsync(System.Text.Encoding.UTF8.GetString(ms.ToArray()).AsMemory(), ct).ConfigureAwait(false);
        }

        public async Task FinishAsync(CancellationToken ct)
        {
            if (_buffer is not null)
            {
                await _buffer.DisposeAsync().ConfigureAwait(false);
                _buffer = null;
            }

            await MiniExcel.SaveAsAsync(_output, Rows(), sheetName: _sheet, excelType: ExcelType.XLSX, cancellationToken: ct).ConfigureAwait(false);
        }

        private IEnumerable<Dictionary<string, object?>> Rows()
        {
            if (_columns.Count == 0)
            {
                yield break;
            }

            foreach (var line in File.ReadLines(_bufferPath))
            {
                using var doc = JsonDocument.Parse(line);
                var r = JsonFormat.ToRecord(doc.RootElement, "tampon");
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var c in _columns)
                {
                    row[c] = r[c] switch
                    {
                        Record or IReadOnlyList<object?> => throw new ConnectorException(ErrorKind.Permanent, $"La colonne {c} contient une structure imbriquée : aplatissez-la dans le mapping."),
                        DateTimeOffset d => d.UtcDateTime,
                        var x => x,
                    };
                }

                yield return row;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_buffer is not null)
            {
                await _buffer.DisposeAsync().ConfigureAwait(false);
            }

            File.Delete(_bufferPath);
        }
    }
}
