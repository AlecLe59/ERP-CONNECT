namespace ErpConnect.Connectors.Sdk;

public sealed record RemoteFile(string Name, long Size, DateTimeOffset LastModified);

/// <summary>
/// Transport de fichiers (dossier local, SFTP, FTP). Tous les chemins sont relatifs à la racine configurée,
/// toute sortie de la racine est refusée. L'écriture est atomique : fichier temporaire puis renommage.
/// </summary>
public interface IFileTransport : IAsyncDisposable
{
    Task<IReadOnlyList<RemoteFile>> ListAsync(string pattern, CancellationToken ct);

    Task<Stream> OpenReadAsync(string path, CancellationToken ct);

    /// <summary>Écrit dans un temporaire puis renomme. Si <paramref name="write"/> échoue, rien n'est visible.</summary>
    Task WriteAtomicAsync(string path, WriteMode mode, Func<Stream, CancellationToken, Task> write, CancellationToken ct);

    Task<bool> ExistsAsync(string path, CancellationToken ct);

    Task DeleteAsync(string path, CancellationToken ct);

    Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct);
}

/// <summary>Contrôle des chemins relatifs fournis par l'utilisateur ou par un fichier distant.</summary>
public static class SafePath
{
    /// <summary>Normalise un chemin relatif en séparateurs '/', refuse '..', les chemins absolus, les lecteurs et les caractères de contrôle.</summary>
    public static string Normalize(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        var p = relative.Replace('\\', '/').Trim();
        if (p.Length == 0 || p.Length > 1024)
        {
            throw new ConnectorException(ErrorKind.Permanent, "Chemin vide ou trop long.");
        }

        if (p.StartsWith('/') || p.Contains(':', StringComparison.Ordinal) || p.Any(char.IsControl))
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Chemin refusé : {Display(p)}.");
        }

        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(s => s is ".." or "." || s.EndsWith('.') || s.EndsWith(' ')))
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Chemin refusé : {Display(p)}.");
        }

        return string.Join('/', parts);
    }

    /// <summary>Résout sous une racine locale et vérifie que le résultat y reste (y compris après résolution des liens).</summary>
    public static string ResolveUnder(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, Normalize(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootWithSep, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Chemin hors de la racine autorisée.");
        }

        return full;
    }

    /// <summary>Filtre de nom simple : * et ? uniquement.</summary>
    public static bool Matches(string name, string pattern)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(pattern);
        return Match(name.AsSpan(), pattern.AsSpan());

        static bool Match(ReadOnlySpan<char> s, ReadOnlySpan<char> p)
        {
            while (true)
            {
                if (p.IsEmpty)
                {
                    return s.IsEmpty;
                }

                if (p[0] == '*')
                {
                    for (var i = 0; i <= s.Length; i++)
                    {
                        if (Match(s[i..], p[1..]))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (s.IsEmpty || (p[0] != '?' && char.ToUpperInvariant(p[0]) != char.ToUpperInvariant(s[0])))
                {
                    return false;
                }

                s = s[1..];
                p = p[1..];
            }
        }
    }

    private static string Display(string p) => p.Length > 80 ? p[..80] + "…" : p;
}

/// <summary>Format de fichier (CSV, JSON, XML, XLSX) : décode un flux en enregistrements et inversement.</summary>
public interface IRecordFormat
{
    string Id { get; }

    IReadOnlyList<ConfigField> Options { get; }

    IAsyncEnumerable<Record> ReadAsync(Stream input, IReadOnlyDictionary<string, string> options, CancellationToken ct);

    IFormatWriter CreateWriter(Stream output, IReadOnlyDictionary<string, string> options);
}

public interface IFormatWriter : IAsyncDisposable
{
    Task WriteAsync(Record record, CancellationToken ct);

    /// <summary>Termine le document (balises fermantes, crochet final, archive XLSX).</summary>
    Task FinishAsync(CancellationToken ct);
}
