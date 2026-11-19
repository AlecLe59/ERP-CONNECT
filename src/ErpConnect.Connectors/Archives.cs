using System.IO.Compression;
using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors;

/// <summary>
/// Lecture et écriture d'archives ZIP. Contrôles : noms d'entrées normalisés (pas de « zip slip »),
/// 10 000 entrées au plus, 2 Gio décompressés au plus, taux de compression borné (bombe zip).
/// </summary>
public static class ZipArchives
{
    public const int MaxEntries = 10_000;
    public const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxRatio = 200;

    /// <summary>Copie le flux dans un fichier temporaire si nécessaire (ZipArchive en lecture exige un flux positionnable).</summary>
    public static async Task<Stream> SeekableAsync(Stream input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.CanSeek)
        {
            return input;
        }

        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await using (input.ConfigureAwait(false))
        {
            await input.CopyToAsync(temp, ct).ConfigureAwait(false);
        }

        temp.Position = 0;
        return temp;
    }

    /// <summary>Entrées valides correspondant au motif, contrôlées. Lève une erreur permanente si l'archive est suspecte.</summary>
    public static IReadOnlyList<string> CheckedEntries(ZipArchive zip, string pattern)
    {
        ArgumentNullException.ThrowIfNull(zip);
        if (zip.Entries.Count > MaxEntries)
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Archive refusée : plus de {MaxEntries} entrées.");
        }

        long total = 0;
        var names = new List<string>();
        foreach (var e in zip.Entries)
        {
            total += e.Length;
            if (total > MaxUncompressedBytes)
            {
                throw new ConnectorException(ErrorKind.Permanent, "Archive refusée : plus de 2 Gio une fois décompressée.");
            }

            if (e.CompressedLength > 0 && e.Length / e.CompressedLength > MaxRatio)
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Archive refusée : taux de compression anormal ({e.FullName}).");
            }

            if (e.FullName.EndsWith('/'))
            {
                continue; // dossier
            }

            var safe = SafePath.Normalize(e.FullName); // refuse ../, chemins absolus, lecteurs
            if (SafePath.Matches(Path.GetFileName(safe), pattern))
            {
                names.Add(e.FullName);
            }
        }

        return names.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Flux d'une entrée qui libère l'archive et le flux source à sa fermeture. La taille lue est plafonnée à la taille déclarée.</summary>
    public static Stream OpenEntry(ZipArchive zip, Stream archiveStream, string entryName)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var entry = zip.GetEntry(entryName) ?? throw new ConnectorException(ErrorKind.Permanent, $"Entrée introuvable : {entryName}.");
        return new OwningStream(entry.Open(), entry.Length, zip, archiveStream);
    }

    private sealed class OwningStream(Stream inner, long declared, IDisposable zip, Stream source) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        private int Count(int n)
        {
            _read += n;
            return _read > declared
                ? throw new ConnectorException(ErrorKind.Permanent, "Entrée d'archive plus grande que sa taille déclarée : refusée.")
                : n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                zip.Dispose();
                source.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
