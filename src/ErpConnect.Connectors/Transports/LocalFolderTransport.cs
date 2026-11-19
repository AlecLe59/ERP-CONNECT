using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors.Transports;

/// <summary>Dossier local ou partage UNC. Tout chemin est résolu sous la racine ; les liens sortant de la racine sont refusés.</summary>
public sealed class LocalFolderTransport : IFileTransport
{
    private readonly string _root;

    public LocalFolderTransport(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    private string Resolve(string relative)
    {
        var full = SafePath.ResolveUnder(_root, relative);
        // Un lien symbolique ou une jonction ne doit pas permettre de sortir de la racine.
        var current = new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (current is not null && current.FullName.Length >= _root.Length)
        {
            if (current.Exists && current.LinkTarget is not null)
            {
                throw new ConnectorException(ErrorKind.Permanent, "Lien symbolique refusé dans le chemin.");
            }

            current = current.Parent;
        }

        if (File.Exists(full) && new FileInfo(full).LinkTarget is not null)
        {
            throw new ConnectorException(ErrorKind.Permanent, "Lien symbolique refusé.");
        }

        return full;
    }

    public Task<IReadOnlyList<RemoteFile>> ListAsync(string pattern, CancellationToken ct)
    {
        var normalized = SafePath.Normalize(pattern);
        var dir = Path.GetDirectoryName(normalized.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var namePattern = Path.GetFileName(normalized);
        var fullDir = dir.Length == 0 ? _root : Resolve(dir + "/x")[..^2];
        if (!Directory.Exists(fullDir))
        {
            return Task.FromResult<IReadOnlyList<RemoteFile>>([]);
        }

        IReadOnlyList<RemoteFile> files = new DirectoryInfo(fullDir).EnumerateFiles()
            .Where(f => f.LinkTarget is null && !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && SafePath.Matches(f.Name, namePattern))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new RemoteFile(dir.Length == 0 ? f.Name : $"{dir.Replace(Path.DirectorySeparatorChar, '/')}/{f.Name}", f.Length, f.LastWriteTimeUtc))
            .ToList();
        return Task.FromResult(files);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Fichier introuvable : {SafePath.Normalize(path)}.");
        }

        return Task.FromResult<Stream>(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public async Task WriteAtomicAsync(string path, WriteMode mode, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (mode == WriteMode.CreateNew && File.Exists(full))
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Le fichier existe déjà : {SafePath.Normalize(path)}.");
        }

        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await write(fs, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            File.Move(temp, full, overwrite: mode == WriteMode.Overwrite);
        }
        catch (IOException ex) when (mode == WriteMode.CreateNew && File.Exists(full))
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Le fichier existe déjà : {SafePath.Normalize(path)}.", ex);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(File.Exists(Resolve(path)));

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        File.Delete(Resolve(path));
        return Task.CompletedTask;
    }

    public Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        var target = Resolve(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(Resolve(sourcePath), target, overwrite: false);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
