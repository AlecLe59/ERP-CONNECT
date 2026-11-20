using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ErpConnect.Connectors.Formats;
using ErpConnect.Connectors.Sdk;
using ErpConnect.Connectors.Transports;

namespace ErpConnect.Connectors;

/// <summary>
/// Connecteur fichier générique : un transport (dossier local, SFTP, FTP) et un format (CSV, JSON, XML, XLSX).
/// Lecture : la ressource est un motif (« entrees/*.csv »), fichiers traités par ordre de nom.
/// Écriture : la ressource est un chemin cible ({runId} et {timestamp} sont remplacés), écrit de façon atomique.
/// </summary>
public abstract class FileConnector : IConnector
{
    public static IReadOnlyList<ConfigField> FormatSelector { get; } =
    [
        new("format", "Format", ConfigFieldType.Choice, Required: true, Choices: FormatRegistry.All.Select(f => f.Id).ToArray()),
        new("archive", "Archive", ConfigFieldType.Choice, Default: "none", Choices: ["none", "zip"]),
        new("entryPattern", "Entrées lues dans l'archive", ConfigFieldType.Text, Default: "*"),
        new("entryName", "Nom de l'entrée écrite dans l'archive", ConfigFieldType.Text),
    ];

    public abstract ConnectorManifest Manifest { get; }

    /// <summary>Crée le transport pour une configuration. Visible pour les tests de conformité.</summary>
    public abstract IFileTransport CreateTransport(ConnectorSettings settings);

    public async Task<ConnectionTestResult> TestAsync(ConnectorSettings settings, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var t = CreateTransport(settings);
            var files = await t.ListAsync("*", ct).ConfigureAwait(false);
            return new ConnectionTestResult(true, $"Connexion réussie, {files.Count} fichier(s) à la racine.", sw.Elapsed);
        }
        catch (ConnectorException ex)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    public async Task<DataStructure> DiscoverAsync(ConnectorSettings settings, string? resource, CancellationToken ct)
    {
        await using var t = CreateTransport(settings);
        var files = await t.ListAsync(string.IsNullOrEmpty(resource) ? "*" : resource, ct).ConfigureAwait(false);
        return new DataStructure(files.Select(f => f.Name).Take(500).ToArray(), []);
    }

    /// <summary>Découvre les champs d'un fichier à partir des premiers enregistrements.</summary>
    public async Task<DataStructure> SampleAsync(ConnectorSettings settings, ReadRequest request, int maxRecords, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = 0;
        await using var reader = CreateReader(settings, request with { BatchSize = Math.Min(maxRecords, 1000) });
        await foreach (var batch in reader.ReadAsync(null, ct).ConfigureAwait(false))
        {
            foreach (var r in batch.Records)
            {
                foreach (var (k, v) in r)
                {
                    fields.TryAdd(k, TypeName(v));
                }

                if (++count >= maxRecords)
                {
                    break;
                }
            }

            if (count >= maxRecords)
            {
                break;
            }
        }

        return new DataStructure([request.Resource], fields.Select(f => new FieldInfo(f.Key, f.Value)).ToArray());
    }

    public static string TypeName(object? v) => v switch
    {
        null => "null",
        string => "string",
        long => "integer",
        decimal or double => "number",
        bool => "boolean",
        DateTimeOffset => "datetime",
        DateOnly => "date",
        byte[] => "binary",
        Record => "object",
        _ => "array",
    };

    public IRecordReader CreateReader(ConnectorSettings settings, ReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Reader(CreateTransport(settings), request);
    }

    public IRecordWriter CreateWriter(ConnectorSettings settings, WriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Writer(CreateTransport(settings), request);
    }

    internal static Dictionary<string, string> FormatOptions(IReadOnlyDictionary<string, string>? options) =>
        options?.Where(kv => kv.Key is not ("format" or "archive" or "entryPattern" or "entryName")).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);

    public static string ExpandTarget(string resource, string? runId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return resource
            .Replace("{runId}", runId ?? "manuel", StringComparison.Ordinal)
            .Replace("{timestamp}", now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{date}", now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private sealed class Reader : IRecordReader
    {
        private readonly IFileTransport _transport;
        private readonly ReadRequest _request;

        public Reader(IFileTransport transport, ReadRequest request)
        {
            _transport = transport;
            _request = request;
        }

        private sealed record Item(string Name, Func<CancellationToken, Task<Stream>> Open);

        /// <summary>Fichiers à lire ; avec archive=zip, chaque entrée correspondante d'archive devient un élément « archive.zip!entrée ».</summary>
        private async Task<List<Item>> ItemsAsync(CancellationToken ct)
        {
            var files = await _transport.ListAsync(_request.Resource, ct).ConfigureAwait(false);
            if (_request.Option("archive") != "zip")
            {
                return files.Select(f => new Item(f.Name, t => _transport.OpenReadAsync(f.Name, t))).ToList();
            }

            var pattern = _request.Option("entryPattern") is { Length: > 0 } p ? p : "*";
            var items = new List<Item>();
            foreach (var f in files)
            {
                var stream = await ZipArchives.SeekableAsync(await _transport.OpenReadAsync(f.Name, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    using var zip = OpenZip(stream, f.Name);
                    foreach (var entry in ZipArchives.CheckedEntries(zip, pattern))
                    {
                        var archive = f.Name;
                        items.Add(new Item($"{archive}!{entry}", async t =>
                        {
                            var s = await ZipArchives.SeekableAsync(await _transport.OpenReadAsync(archive, t).ConfigureAwait(false), t).ConfigureAwait(false);
                            return ZipArchives.OpenEntry(OpenZip(s, archive), s, entry);
                        }));
                    }
                }
            }

            return items;
        }

        private static System.IO.Compression.ZipArchive OpenZip(Stream s, string name)
        {
            try
            {
                return new System.IO.Compression.ZipArchive(s, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException ex)
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Archive ZIP illisible : {name}.", ex);
            }
        }

        public async IAsyncEnumerable<RecordBatch> ReadAsync(string? cursor, [EnumeratorCancellation] CancellationToken ct)
        {
            var format = FormatRegistry.Get(_request.Option("format"));
            var options = FormatOptions(_request.Options);
            var files = await ItemsAsync(ct).ConfigureAwait(false);
            var (startFile, skip) = ParseCursor(cursor, files.Select(f => f.Name).ToList());
            var batchSize = Math.Clamp(_request.BatchSize, 1, 100_000);
            for (var fi = startFile; fi < files.Count; fi++)
            {
                var batch = new List<Record>(batchSize);
                long index = 0;
                var stream = await files[fi].Open(ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    await foreach (var record in format.ReadAsync(stream, options, ct).ConfigureAwait(false))
                    {
                        index++;
                        if (fi == startFile && index <= skip)
                        {
                            continue;
                        }

                        batch.Add(record);
                        if (batch.Count == batchSize)
                        {
                            yield return new RecordBatch(batch, Cursor(fi, index, files[fi].Name));
                            batch = new List<Record>(batchSize);
                        }
                    }
                }

                if (batch.Count > 0 || fi == files.Count - 1)
                {
                    yield return new RecordBatch(batch, Cursor(fi + 1, 0, fi + 1 < files.Count ? files[fi + 1].Name : string.Empty));
                }
            }
        }

        private static string Cursor(int file, long records, string name) => string.Create(CultureInfo.InvariantCulture, $"{file}:{records}:{name}");

        private static (int File, long Skip) ParseCursor(string? cursor, IReadOnlyList<string> files)
        {
            if (string.IsNullOrEmpty(cursor))
            {
                return (0, 0);
            }

            var parts = cursor.Split(':', 3);
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var f) || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                throw new ConnectorException(ErrorKind.Permanent, "Point de reprise illisible.");
            }

            if (f < files.Count && !string.Equals(files[f], parts[2], StringComparison.Ordinal))
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Les fichiers source ont changé depuis le point de reprise (attendu {parts[2]}).");
            }

            return (f, n);
        }

        public ValueTask DisposeAsync() => _transport.DisposeAsync();
    }

    private sealed class Writer : IRecordWriter
    {
        private readonly IFileTransport _transport;
        private readonly Channel<(IReadOnlyList<Record> Batch, TaskCompletionSource Done)> _channel =
            Channel.CreateBounded<(IReadOnlyList<Record>, TaskCompletionSource)>(1);
        private readonly CancellationTokenSource _abort = new();
        private readonly WriteRequest _request;
        private Task? _pump;

        public Writer(IFileTransport transport, WriteRequest request)
        {
            _transport = transport;
            _request = request;
        }

        private Task Pump()
        {
            var format = FormatRegistry.Get(_request.Option("format"));
            var options = FormatOptions(_request.Options);
            var target = ExpandTarget(_request.Resource, _request.RunId, DateTimeOffset.UtcNow);
            var zip = _request.Option("archive") == "zip";
            return _transport.WriteAtomicAsync(target, _request.Mode, async (output, ct) =>
            {
                System.IO.Compression.ZipArchive? archive = null;
                var stream = output;
                if (zip)
                {
                    archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);
                    var entryName = _request.Option("entryName") is { Length: > 0 } en ? SafePath.Normalize(en) : Path.GetFileNameWithoutExtension(target) + "." + format.Id;
                    stream = await archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal).OpenAsync(ct).ConfigureAwait(false);
                }

                using var archiveScope = archive; // libéré en dernier : l'archive écrit son répertoire central
                await using var entryScope = zip ? stream : null; // l'entrée se ferme avant l'archive
                var writer = format.CreateWriter(stream, options);
                await using (writer.ConfigureAwait(false))
                {
                    await foreach (var (batch, done) in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        try
                        {
                            foreach (var r in batch)
                            {
                                await writer.WriteAsync(r, ct).ConfigureAwait(false);
                            }

                            done.SetResult();
                        }
                        catch (Exception ex)
                        {
                            done.SetException(ex);
                            throw;
                        }
                    }

                    await writer.FinishAsync(ct).ConfigureAwait(false);
                }
            }, _abort.Token);
        }

        public async Task<WriteResult> WriteBatchAsync(IReadOnlyList<Record> batch, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(batch);
            _pump ??= Task.Run(Pump, CancellationToken.None);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await _channel.Writer.WriteAsync((batch, done), ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                await _pump.ConfigureAwait(false);
                throw;
            }

            await Task.WhenAny(done.Task, _pump).ConfigureAwait(false);
            if (_pump.IsFaulted)
            {
                await _pump.ConfigureAwait(false);
            }

            await done.Task.ConfigureAwait(false);
            return WriteResult.AllSucceeded(batch.Count);
        }

        public async Task CompleteAsync(CancellationToken ct)
        {
            _pump ??= Task.Run(Pump, CancellationToken.None);
            _channel.Writer.TryComplete();
            await _pump.WaitAsync(ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_pump is not null && !_pump.IsCompleted)
            {
                // Abandon sans CompleteAsync : le temporaire est supprimé, rien n'est publié.
                await _abort.CancelAsync().ConfigureAwait(false);
                _channel.Writer.TryComplete(new OperationCanceledException());
                try
                {
                    await _pump.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ConnectorException or IOException or ChannelClosedException)
                {
                }
            }

            _abort.Dispose();
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class LocalFolderConnector : FileConnector
{
    public override ConnectorManifest Manifest { get; } = new(
        "file.local", "Dossier local ou partage réseau", "1.0.0",
        ConnectorCapabilities.Read | ConnectorCapabilities.Write | ConnectorCapabilities.Discover | ConnectorCapabilities.FileTransport,
        [new("rootPath", "Dossier racine", ConfigFieldType.Path, Required: true, Help: "Chemin local ou UNC. Rien n'est lu ni écrit hors de ce dossier.")],
        "Fichiers CSV, JSON, XML, XLSX dans un dossier du serveur.");

    public override IFileTransport CreateTransport(ConnectorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new LocalFolderTransport(settings.Require("rootPath"));
    }
}

public sealed class FtpConnector : FileConnector
{
    public override ConnectorManifest Manifest { get; } = new(
        "file.ftp", "Serveur FTP / FTPS", "1.0.0",
        ConnectorCapabilities.Read | ConnectorCapabilities.Write | ConnectorCapabilities.Discover | ConnectorCapabilities.FileTransport,
        [
            new("host", "Hôte", ConfigFieldType.Text, Required: true),
            new("port", "Port", ConfigFieldType.Number, Default: "21"),
            new("username", "Utilisateur", ConfigFieldType.Text, Required: true),
            new("password", "Mot de passe", ConfigFieldType.Text, Secret: true),
            new("encryption", "Chiffrement", ConfigFieldType.Choice, Default: "explicit", Choices: ["explicit", "implicit", "none"]),
            new("allowPlainFtp", "Autoriser le FTP non chiffré", ConfigFieldType.Boolean, Default: "false"),
            new("certificateFingerprint", "Empreinte SHA-256 du certificat (épinglage)", ConfigFieldType.Text, Help: "Pour un certificat auto-signé. Vide = validation Windows."),
            new("rootPath", "Dossier racine distant", ConfigFieldType.Text, Default: "/"),
            new("timeoutSeconds", "Délai (s)", ConfigFieldType.Number, Default: "30"),
        ],
        "Fichiers sur un serveur FTP, FTPS explicite ou implicite.");

    public override IFileTransport CreateTransport(ConnectorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new FtpTransport(new FtpOptions(
            settings.Require("host"), settings.GetInt("port", 21), settings.Require("username"), settings.GetSecret("password"),
            settings.Get("encryption") ?? "explicit", settings.GetBool("allowPlainFtp"), settings.Get("certificateFingerprint"),
            settings.Get("rootPath") ?? "/", TimeSpan.FromSeconds(Math.Clamp(settings.GetInt("timeoutSeconds", 30), 1, 600))));
    }
}

public sealed class SftpConnector : FileConnector
{
    public override ConnectorManifest Manifest { get; } = new(
        "file.sftp", "Serveur SFTP", "1.0.0",
        ConnectorCapabilities.Read | ConnectorCapabilities.Write | ConnectorCapabilities.Discover | ConnectorCapabilities.FileTransport,
        [
            new("host", "Hôte", ConfigFieldType.Text, Required: true),
            new("port", "Port", ConfigFieldType.Number, Default: "22"),
            new("username", "Utilisateur", ConfigFieldType.Text, Required: true),
            new("password", "Mot de passe", ConfigFieldType.Text, Secret: true),
            new("privateKey", "Clé privée (PEM/OpenSSH)", ConfigFieldType.Text, Secret: true),
            new("privateKeyPassphrase", "Phrase de passe de la clé", ConfigFieldType.Text, Secret: true),
            new("hostKeyFingerprint", "Empreinte de la clé d'hôte", ConfigFieldType.Text, Required: true, Help: "Format SHA256:… (ssh-keygen -lf)."),
            new("rootPath", "Dossier racine distant", ConfigFieldType.Text, Default: "/"),
            new("timeoutSeconds", "Délai (s)", ConfigFieldType.Number, Default: "30"),
        ],
        "Fichiers sur un serveur SFTP, clé d'hôte vérifiée.");

    public override IFileTransport CreateTransport(ConnectorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SftpTransport(new SftpOptions(
            settings.Require("host"), settings.GetInt("port", 22), settings.Require("username"),
            settings.GetSecret("password"), settings.GetSecret("privateKey"), settings.GetSecret("privateKeyPassphrase"),
            settings.Require("hostKeyFingerprint"), settings.Get("rootPath") ?? "/", TimeSpan.FromSeconds(Math.Clamp(settings.GetInt("timeoutSeconds", 30), 1, 600))));
    }
}
