using System.Security.Cryptography;
using ErpConnect.Connectors.Sdk;
using FluentFTP;
using FluentFTP.Exceptions;

namespace ErpConnect.Connectors.Transports;

/// <summary>encryption : explicit (FTPS AUTH TLS, défaut), implicit (FTPS port 990), none (FTP clair, refusé sauf autorisation explicite).</summary>
public sealed record FtpOptions(
    string Host,
    int Port,
    string Username,
    string? Password,
    string Encryption,
    bool AllowPlainFtp,
    string? CertificateFingerprint,
    string RootPath,
    TimeSpan Timeout);

/// <summary>
/// FTP/FTPS via FluentFTP, mode passif. Le certificat du serveur est validé par la chaîne de confiance Windows,
/// ou épinglé par son empreinte SHA-256 si elle est fournie (certificat auto-signé).
/// Écriture : fichier local temporaire, envoi sous un nom temporaire distant, puis renommage.
/// Limite : FTP n'a pas de renommage atomique avec remplacement ; en mode « Remplacer », la cible est supprimée juste avant le renommage.
/// </summary>
public sealed class FtpTransport : IFileTransport
{
    private readonly FtpOptions _options;
    private readonly string _root;
    private AsyncFtpClient? _client;

    public FtpTransport(FtpOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.Encryption is not ("explicit" or "implicit" or "none"))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Chiffrement : explicit, implicit ou none.");
        }

        if (options.Encryption == "none" && !options.AllowPlainFtp)
        {
            throw new ConnectorException(ErrorKind.Permanent, "FTP non chiffré refusé : utilisez FTPS ou autorisez explicitement le FTP clair.");
        }

        _root = "/" + options.RootPath.Replace('\\', '/').Trim('/');
        if (_root.Split('/').Any(s => s == ".."))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Racine FTP invalide.");
        }
    }

    public static string Fingerprint(byte[] rawCertificate) => Convert.ToHexString(SHA256.HashData(rawCertificate));

    public static bool FingerprintMatches(string expected, string actualHex)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var clean = expected.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return clean.Length == 64 && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(clean), System.Text.Encoding.ASCII.GetBytes(actualHex));
    }

    private string Remote(string relative) => _root == "/" ? "/" + SafePath.Normalize(relative) : $"{_root}/{SafePath.Normalize(relative)}";

    private async Task<AsyncFtpClient> ClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
        {
            return _client;
        }

        var client = new AsyncFtpClient(_options.Host, _options.Username, _options.Password ?? string.Empty, _options.Port);
        client.Config.EncryptionMode = _options.Encryption switch
        {
            "implicit" => FtpEncryptionMode.Implicit,
            "explicit" => FtpEncryptionMode.Explicit,
            _ => FtpEncryptionMode.None,
        };
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.DataConnectionEncryption = _options.Encryption != "none";
        client.Config.ValidateAnyCertificate = false;
        client.Config.ConnectTimeout = (int)_options.Timeout.TotalMilliseconds;
        client.Config.ReadTimeout = (int)_options.Timeout.TotalMilliseconds;
        client.Config.DataConnectionConnectTimeout = (int)_options.Timeout.TotalMilliseconds;
        client.Config.DataConnectionReadTimeout = (int)_options.Timeout.TotalMilliseconds;
        string? seen = null;
        client.ValidateCertificate += (_, e) =>
        {
            seen = e.Certificate is null ? null : Fingerprint(e.Certificate.GetRawCertData());
            e.Accept = !string.IsNullOrWhiteSpace(_options.CertificateFingerprint)
                ? seen is not null && FingerprintMatches(_options.CertificateFingerprint, seen)
                : e.PolicyErrors == System.Net.Security.SslPolicyErrors.None;
        };
        try
        {
            await client.Connect(ct).ConfigureAwait(false);
        }
        catch (FtpAuthenticationException ex)
        {
            client.Dispose();
            throw new ConnectorException(ErrorKind.Permanent, "Authentification FTP refusée.", ex);
        }
        catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException || ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            client.Dispose();
            throw new ConnectorException(ErrorKind.Permanent, $"Certificat du serveur refusé{(seen is null ? string.Empty : $" (SHA-256 {seen})")}.", ex);
        }
        catch (Exception ex) when (ex is FtpException or IOException or System.Net.Sockets.SocketException or TimeoutException)
        {
            client.Dispose();
            throw new ConnectorException(ErrorKind.Transient, $"Connexion FTP impossible : {ex.Message}", ex);
        }

        _client = client;
        return client;
    }

    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string pattern, CancellationToken ct)
    {
        var normalized = SafePath.Normalize(pattern);
        var slash = normalized.LastIndexOf('/');
        var dir = slash < 0 ? string.Empty : normalized[..slash];
        var namePattern = slash < 0 ? normalized : normalized[(slash + 1)..];
        var client = await ClientAsync(ct).ConfigureAwait(false);
        var remoteDir = dir.Length == 0 ? _root : Remote(dir);
        if (!await client.DirectoryExists(remoteDir, ct).ConfigureAwait(false))
        {
            return [];
        }

        var items = await client.GetListing(remoteDir, ct).ConfigureAwait(false);
        return items.Where(i => i.Type == FtpObjectType.File && !i.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && SafePath.Matches(i.Name, namePattern))
            .Select(i => new RemoteFile(dir.Length == 0 ? i.Name : $"{dir}/{i.Name}", i.Size, new DateTimeOffset(DateTime.SpecifyKind(i.Modified, DateTimeKind.Utc))))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Téléchargé dans un fichier temporaire supprimé à la fermeture : la connexion de contrôle reste libre.</summary>
    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        var remote = Remote(path);
        if (!await client.FileExists(remote, ct).ConfigureAwait(false))
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Fichier distant introuvable : {SafePath.Normalize(path)}.");
        }

        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            if (!await client.DownloadStream(temp, remote, token: ct).ConfigureAwait(false))
            {
                throw new ConnectorException(ErrorKind.Transient, "Téléchargement FTP incomplet.");
            }
        }
        catch (FtpException ex)
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw new ConnectorException(ErrorKind.Transient, $"Téléchargement FTP interrompu : {ex.Message}", ex);
        }

        temp.Position = 0;
        return temp;
    }

    public async Task WriteAtomicAsync(string path, WriteMode mode, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        var client = await ClientAsync(ct).ConfigureAwait(false);
        var target = Remote(path);
        if (mode == WriteMode.CreateNew && await client.FileExists(target, ct).ConfigureAwait(false))
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Le fichier distant existe déjà : {SafePath.Normalize(path)}.");
        }

        var slash = target.LastIndexOf('/');
        var dir = slash == 0 ? "/" : target[..slash];
        var temp = $"{dir.TrimEnd('/')}/.{target[(slash + 1)..]}.{Guid.NewGuid():N}.tmp";
        await using var local = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await write(local, ct).ConfigureAwait(false);
        local.Position = 0;
        try
        {
            if (await client.UploadStream(local, temp, FtpRemoteExists.NoCheck, createRemoteDir: true, token: ct).ConfigureAwait(false) != FtpStatus.Success)
            {
                throw new ConnectorException(ErrorKind.Transient, "Envoi FTP refusé.");
            }

            if (mode == WriteMode.Overwrite && await client.FileExists(target, ct).ConfigureAwait(false))
            {
                await client.DeleteFile(target, ct).ConfigureAwait(false);
            }

            await client.Rename(temp, target, ct).ConfigureAwait(false);
        }
        catch (FtpException ex)
        {
            throw new ConnectorException(ErrorKind.Transient, $"Écriture FTP interrompue : {ex.Message}", ex);
        }
        finally
        {
            if (client.IsConnected && await client.FileExists(temp, CancellationToken.None).ConfigureAwait(false))
            {
                await client.DeleteFile(temp, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct) => await (await ClientAsync(ct).ConfigureAwait(false)).FileExists(Remote(path), ct).ConfigureAwait(false);

    public async Task DeleteAsync(string path, CancellationToken ct) => await (await ClientAsync(ct).ConfigureAwait(false)).DeleteFile(Remote(path), ct).ConfigureAwait(false);

    public async Task MoveAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        var target = Remote(targetPath);
        var dir = target[..target.LastIndexOf('/')];
        if (dir.Length > 0)
        {
            await client.CreateDirectory(dir, true, ct).ConfigureAwait(false);
        }

        await client.Rename(Remote(sourcePath), target, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            if (_client.IsConnected)
            {
                try
                {
                    await _client.Disconnect(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is FtpException or IOException or TimeoutException)
                {
                }
            }

            _client.Dispose();
            _client = null;
        }
    }
}
