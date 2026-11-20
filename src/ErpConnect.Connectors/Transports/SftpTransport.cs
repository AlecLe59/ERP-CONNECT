using ErpConnect.Connectors.Sdk;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ErpConnect.Connectors.Transports;

/// <summary>Paramètres SFTP. L'empreinte de la clé d'hôte est obligatoire : aucune connexion à un serveur inconnu.</summary>
public sealed record SftpOptions(
    string Host,
    int Port,
    string Username,
    string? Password,
    string? PrivateKey,
    string? PrivateKeyPassphrase,
    string HostKeyFingerprint,
    string RootPath,
    TimeSpan Timeout);

/// <summary>
/// SFTP via SSH.NET. Écriture atomique : envoi dans un temporaire puis renommage POSIX (posix-rename@openssh.com).
/// La clé d'hôte est comparée à l'empreinte SHA-256 configurée (format « SHA256:… » d'OpenSSH).
/// </summary>
public sealed class SftpTransport : IFileTransport
{
    private readonly SftpOptions _options;
    private readonly string _root;
    private SftpClient? _client;

    public SftpTransport(SftpOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.HostKeyFingerprint))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Empreinte de clé d'hôte obligatoire.");
        }

        _root = "/" + options.RootPath.Replace('\\', '/').Trim('/');
        if (_root.Contains("/../", StringComparison.Ordinal) || _root.EndsWith("/..", StringComparison.Ordinal))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Racine SFTP invalide.");
        }
    }

    /// <summary>Compare deux empreintes en ignorant le préfixe « SHA256: » et le remplissage base64.</summary>
    public static bool FingerprintMatches(string expected, string actualBase64)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actualBase64);
        static string Clean(string s) => (s.StartsWith("SHA256:", StringComparison.Ordinal) ? s[7..] : s).TrimEnd('=').Trim();
        var a = Clean(expected);
        var b = Clean(actualBase64);
        return a.Length > 0 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));
    }

    private async Task<SftpClient> ClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
        {
            return _client;
        }

        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(_options.PrivateKey))
        {
            using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_options.PrivateKey));
            var key = string.IsNullOrEmpty(_options.PrivateKeyPassphrase) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, _options.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(_options.Username, key));
        }

        if (!string.IsNullOrEmpty(_options.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(_options.Username, _options.Password));
        }

        if (methods.Count == 0)
        {
            throw new ConnectorException(ErrorKind.Permanent, "Mot de passe ou clé privée requis.");
        }

        var info = new ConnectionInfo(_options.Host, _options.Port, _options.Username, methods.ToArray()) { Timeout = _options.Timeout };
        var client = new SftpClient(info) { OperationTimeout = _options.Timeout };
        string? seen = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = e.FingerPrintSHA256;
            e.CanTrust = FingerprintMatches(_options.HostKeyFingerprint, e.FingerPrintSHA256);
        };
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (SshConnectionException ex) when (seen is not null && !FingerprintMatches(_options.HostKeyFingerprint, seen))
        {
            client.Dispose();
            throw new ConnectorException(ErrorKind.Permanent, $"Clé d'hôte inattendue (SHA256:{seen}). Connexion refusée.", ex);
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new ConnectorException(ErrorKind.Permanent, "Authentification SFTP refusée.", ex);
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException)
        {
            client.Dispose();
            throw new ConnectorException(ErrorKind.Transient, $"Connexion SFTP impossible : {ex.Message}", ex);
        }

        _client = client;
        return client;
    }

    private string Remote(string relative) => _root == "/" ? "/" + SafePath.Normalize(relative) : $"{_root}/{SafePath.Normalize(relative)}";

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string pattern, CancellationToken ct)
    {
        var normalized = SafePath.Normalize(pattern);
        var slash = normalized.LastIndexOf('/');
        var dir = slash < 0 ? string.Empty : normalized[..slash];
        var namePattern = slash < 0 ? normalized : normalized[(slash + 1)..];
        var client = await ClientAsync(ct).ConfigureAwait(false);
        var remoteDir = dir.Length == 0 ? _root : Remote(dir);
        var list = new List<RemoteFile>();
        try
        {
            await foreach (var f in client.ListDirectoryAsync(remoteDir, ct).ConfigureAwait(false))
            {
                if (f.IsRegularFile && !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && SafePath.Matches(f.Name, namePattern))
                {
                    list.Add(new RemoteFile(dir.Length == 0 ? f.Name : $"{dir}/{f.Name}", f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)));
                }
            }
        }
        catch (SftpPathNotFoundException)
        {
            return [];
        }

        return list.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        try
        {
            return await client.OpenAsync(Remote(path), FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Fichier distant introuvable : {SafePath.Normalize(path)}.", ex);
        }
    }

    public async Task WriteAtomicAsync(string path, WriteMode mode, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        var client = await ClientAsync(ct).ConfigureAwait(false);
        var target = Remote(path);
        if (mode == WriteMode.CreateNew && client.Exists(target))
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Le fichier distant existe déjà : {SafePath.Normalize(path)}.");
        }

        var slash = target.LastIndexOf('/');
        var temp = $"{target[..slash]}/.{target[(slash + 1)..]}.{Guid.NewGuid():N}.tmp";
        await EnsureDirectoryAsync(client, target[..slash], ct).ConfigureAwait(false);
        try
        {
            await using (var remote = await client.OpenAsync(temp, FileMode.CreateNew, FileAccess.Write, ct).ConfigureAwait(false))
            {
                await write(remote, ct).ConfigureAwait(false);
                await remote.FlushAsync(ct).ConfigureAwait(false);
            }

            // posix-rename remplace la cible de façon atomique si elle existe (Overwrite) ; CreateNew a été contrôlé plus haut.
            client.RenameFile(temp, target, isPosix: mode == WriteMode.Overwrite);
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
            throw new ConnectorException(ErrorKind.Transient, $"Écriture SFTP interrompue : {ex.Message}", ex);
        }
        finally
        {
            if (client.IsConnected && client.Exists(temp))
            {
                client.DeleteFile(temp);
            }
        }
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct) => (await ClientAsync(ct).ConfigureAwait(false)).Exists(Remote(path));

    public async Task DeleteAsync(string path, CancellationToken ct) => await (await ClientAsync(ct).ConfigureAwait(false)).DeleteFileAsync(Remote(path), ct).ConfigureAwait(false);

    public async Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        var target = Remote(targetPath);
        await EnsureDirectoryAsync(client, target[..target.LastIndexOf('/')], ct).ConfigureAwait(false);
        await client.RenameFileAsync(Remote(sourcePath), target, ct).ConfigureAwait(false);
    }

    private async Task EnsureDirectoryAsync(SftpClient client, string directory, CancellationToken ct)
    {
        if (directory.Length <= _root.Length || client.Exists(directory))
        {
            return;
        }

        var current = _root == "/" ? string.Empty : _root;
        foreach (var segment in directory[_root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (!client.Exists(current))
            {
                await client.CreateDirectoryAsync(current, ct).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}
