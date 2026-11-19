using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Audit;
using ErpConnect.Core.Authorization;
using ErpConnect.Security.Backup;
using ErpConnect.Security.Vault;
using ErpConnect.Storage;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Engine;

#pragma warning disable CA1819 // Forme sérialisée.

public sealed record BackupFileEntry(string Name, long Size, string Sha256, int SchemaVersion);

public sealed record BackupManifest(int Format, string ProductVersion, string InstallationId, DateTimeOffset CreatedAt, string CreatedBy, bool IncludesKeys, BackupFileEntry[] Files);

public sealed record BackupInfo(string FileName, long Size, DateTimeOffset CreatedAt, bool IncludesKeys);

/// <summary>
/// Demande de restauration déposée par la commande « restore » (administrateur), exécutée par le service à son démarrage,
/// sous son propre compte (les clés restaurées doivent être protégées par le DPAPI du compte du service).
/// protectedPassphrase : phrase de passe protégée par DPAPI machine (base64), effacée après usage.
/// </summary>
public sealed record RestoreRequest(string ArchivePath, bool DiscardSecrets, string? ProtectedPassphrase, DateTimeOffset RequestedAt, string RequestedBy)
{
    public const string FileName = "restore-request.json";
}

public sealed record RestoreReport(BackupManifest Manifest, string PreviousStateFolder, bool KeysRestored, bool SecretsDiscarded);

/// <summary>
/// Sauvegarde à chaud cohérente (VACUUM INTO) des trois bases, avec manifeste et empreintes SHA-256, dans backup\.
/// Les clés maîtres ne sont incluses que si une phrase de passe est fournie (export Argon2id + AES-GCM) :
/// sans elles, les secrets ne se restaurent que sur la même machine (DPAPI).
/// La restauration se fait service arrêté (commande « restore » du service), par <see cref="RestoreAsync"/>.
/// </summary>
public sealed class BackupService : IDisposable
{
    public const string ManifestName = "manifest.json";
    public const string KeysName = "keys.json";
    public const string RestoreMarker = "restored.json";
    private const string Prefix = "erpconnect-backup-";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, MaxDepth = 16 };
    private static readonly string[] DatabaseFiles = ["config.db", "runs.db", "audit.db"];

    private readonly EnginePaths _paths;
    private readonly IReadOnlyList<SqliteDatabase> _databases;
    private readonly MasterKeyStore _keys;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;
    private readonly string _installationId;
    private readonly string _version;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(EnginePaths paths, IReadOnlyList<SqliteDatabase> databases, MasterKeyStore keys, IAuditLog audit, IClock clock, string installationId, string version)
    {
        _paths = paths;
        _databases = databases;
        _keys = keys;
        _audit = audit;
        _clock = clock;
        _installationId = installationId;
        _version = version;
    }

    /// <summary>Paramètres Argon2id de l'export (réduits par les tests).</summary>
    public Argon2Parameters KeyExportParameters { get; init; } = Argon2Parameters.Default;

    public async Task<BackupInfo> CreateAsync(CallerIdentity caller, string? passphrase, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (passphrase is not null)
        {
            try
            {
                KeyEscrow.CheckPassphrase(passphrase);
            }
            catch (ArgumentException ex)
            {
                throw new EngineException(EngineErrorKind.Validation, ex.Message, [new Core.Settings.FieldError("passphrase", ex.Message)]);
            }
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var work = Path.Combine(_paths.Backup, ".work-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var files = new List<BackupFileEntry>();
            foreach (var db in _databases)
            {
                var name = db.Name + ".db";
                var snapshot = Path.Combine(work, name);
                await using (var c = await db.OpenAsync(ct).ConfigureAwait(false))
                {
                    await using var cmd = Cmd(c, "VACUUM INTO $p;");
                    cmd.Parameters.AddWithValue("$p", snapshot);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                files.Add(new BackupFileEntry(name, new FileInfo(snapshot).Length, await HashAsync(snapshot, ct).ConfigureAwait(false),
                    await SchemaVersionAsync(snapshot, ct).ConfigureAwait(false)));
            }

            var now = _clock.UtcNow;
            var manifest = new BackupManifest(1, _version, _installationId, now, caller.UserName, passphrase is not null, [.. files]);
            var fileName = Prefix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6] + ".zip";
            var target = Path.Combine(_paths.Backup, fileName);
            var temp = target + ".tmp";
            await using (var zipStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create);
                foreach (var f in files)
                {
                    await zip.CreateEntryFromFileAsync(Path.Combine(work, f.Name), f.Name, CompressionLevel.Optimal, ct).ConfigureAwait(false);
                }

                if (passphrase is not null)
                {
                    var keys = _keys.ExistingVersions().ToDictionary(v => v, _keys.Get);
                    try
                    {
                        await WriteEntryAsync(zip, KeysName, KeyEscrow.Export(keys, passphrase, KeyExportParameters), ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        foreach (var k in keys.Values)
                        {
                            CryptographicOperations.ZeroMemory(k);
                        }
                    }
                }

                await WriteEntryAsync(zip, ManifestName, JsonSerializer.Serialize(manifest, Json), ct).ConfigureAwait(false);
            }

            File.Move(temp, target);
            var info = new BackupInfo(fileName, new FileInfo(target).Length, now, passphrase is not null);
            await _audit.AppendAsync(new AuditEntry(caller.UserSid, caller.UserName, "backup.create", $"backup/{fileName}", AuditOutcomes.Success,
                JsonSerializer.Serialize(new { info.Size, includesKeys = info.IncludesKeys }), correlationId), ct).ConfigureAwait(false);
            return info;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }

            _gate.Release();
        }
    }

    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(_paths.Backup))
        {
            return [];
        }

        var list = new List<BackupInfo>();
        foreach (var file in Directory.EnumerateFiles(_paths.Backup, Prefix + "*.zip"))
        {
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var manifest = ReadManifest(zip);
                list.Add(new BackupInfo(Path.GetFileName(file), new FileInfo(file).Length, manifest.CreatedAt, manifest.IncludesKeys));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
            {
                // archive illisible : ignorée dans la liste
            }
        }

        return [.. list.OrderByDescending(b => b.CreatedAt)];
    }

    /// <summary>
    /// Restauration, service ARRÊTÉ. L'état actuel (bases, et clés si l'archive en contient) est déplacé dans
    /// backup\pre-restore-&lt;date&gt; avant écriture : rien n'est supprimé. Sans clés dans l'archive, les clés locales sont gardées ;
    /// si elles ne correspondent pas, la restauration est refusée sauf <paramref name="discardSecrets"/> (secrets à ressaisir).
    /// </summary>
    public static async Task<RestoreReport> RestoreAsync(string archivePath, EnginePaths target, Func<byte[], IKeyProtector> protectorFactory, string? passphrase, bool discardSecrets, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(protectorFactory);
        target.EnsureCreated();
        var work = Path.Combine(target.Backup, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            BackupManifest manifest;
            Dictionary<int, byte[]>? keys = null;
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                manifest = ReadManifest(zip);
                if (manifest.Format != 1 || manifest.Files.Length != DatabaseFiles.Length || !manifest.Files.Select(f => f.Name).Order(StringComparer.Ordinal).SequenceEqual(DatabaseFiles.Order(StringComparer.Ordinal)))
                {
                    throw new InvalidDataException("Manifeste de sauvegarde non pris en charge.");
                }

                foreach (var f in manifest.Files)
                {
                    var entry = zip.GetEntry(f.Name) ?? throw new InvalidDataException($"Fichier absent de l'archive : {f.Name}.");
                    var dest = Path.Combine(work, f.Name);
                    await ExtractBoundedAsync(entry, dest, f.Size, ct).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(await HashAsync(dest, ct).ConfigureAwait(false)), Convert.FromHexString(f.Sha256)))
                    {
                        throw new InvalidDataException($"Empreinte incorrecte : {f.Name} (archive altérée ou incomplète).");
                    }
                }

                if (manifest.IncludesKeys && passphrase is not null)
                {
                    var entry = zip.GetEntry(KeysName) ?? throw new InvalidDataException("Export des clés absent de l'archive.");
                    using var reader = new StreamReader(entry.Open());
                    var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    keys = KeyEscrow.Import(text, passphrase); // CryptographicException si la phrase est fausse : rien n'a encore été modifié
                }
            }

            // Versions de clés référencées par la configuration restaurée.
            var referenced = await KeyVersionsAsync(Path.Combine(work, "config.db"), ct).ConfigureAwait(false);
            var protector = protectorFactory(await KeyEntropyAsync(Path.Combine(work, "config.db"), ct).ConfigureAwait(false)); // entropie DPAPI de l'installation restaurée
            var samples = await SampleSecretsAsync(Path.Combine(work, "config.db"), ct).ConfigureAwait(false);
            List<int> missing;
            using (var localKeys = new MasterKeyStore(target.Keys, protector))
            {
                // Une clé est « disponible » si elle déchiffre réellement un secret de la sauvegarde (une clé locale de même numéro peut être une autre clé).
                missing = keys is null
                    ? referenced.Where(v => !Opens(() => localKeys.Get(v), samples.GetValueOrDefault(v))).ToList()
                    : referenced.Where(v => !keys.TryGetValue(v, out var k) || !Opens(() => (byte[])k.Clone(), samples.GetValueOrDefault(v))).ToList();
            }
            if (missing.Count > 0 && !discardSecrets)
            {
                throw new InvalidOperationException(manifest.IncludesKeys && passphrase is null
                    ? "L'archive contient les clés : indiquez la phrase de passe, ou acceptez de perdre les secrets."
                    : "Les clés de cette sauvegarde ne sont pas disponibles sur cette machine : les secrets des connexions seraient illisibles. "
                      + "Utilisez une sauvegarde avec export des clés, ou acceptez de perdre les secrets (à ressaisir).");
            }

            // À partir d'ici, l'état actuel est déplacé (jamais supprimé).
            var previous = Path.Combine(target.Backup, "pre-restore-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(previous);
            SqliteConnection.ClearAllPools();
            foreach (var name in DatabaseFiles)
            {
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    var current = Path.Combine(target.Data, name + suffix);
                    if (File.Exists(current))
                    {
                        File.Move(current, Path.Combine(previous, name + suffix));
                    }
                }
            }

            var discarded = missing.Count > 0;
            if (keys is not null || discarded)
            {
                // Clés locales mises de côté : remplacées par celles de l'archive, ou abandonnées avec les secrets (le coffre en recrée une).
                var keysBackup = Path.Combine(previous, "keys");
                Directory.CreateDirectory(keysBackup);
                foreach (var file in Directory.EnumerateFiles(target.Keys, "master.v*.key"))
                {
                    File.Move(file, Path.Combine(keysBackup, Path.GetFileName(file)));
                }

                if (keys is not null && !discarded)
                {
                    using var store = new MasterKeyStore(target.Keys, protector);
                    foreach (var (version, key) in keys)
                    {
                        store.Import(version, key);
                        CryptographicOperations.ZeroMemory(key);
                    }
                }
            }

            foreach (var name in DatabaseFiles)
            {
                File.Move(Path.Combine(work, name), Path.Combine(target.Data, name));
            }

            if (discarded)
            {
                await DiscardSecretsAsync(Path.Combine(target.Data, "config.db"), ct).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(Path.Combine(target.Data, RestoreMarker), JsonSerializer.Serialize(new
            {
                restoredAt = now,
                backupCreatedAt = manifest.CreatedAt,
                backupVersion = manifest.ProductVersion,
                keysRestored = keys is not null && !discarded,
                secretsDiscarded = discarded,
                previousState = previous,
            }, Json), ct).ConfigureAwait(false);
            return new RestoreReport(manifest, previous, keys is not null && !discarded, discarded);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose() => _gate.Dispose();

    private static SqliteCommand Cmd(SqliteConnection c, string sql)
    {
        var cmd = c.CreateCommand();
#pragma warning disable CA2100 // Requêtes constantes ; le seul paramètre passe par $p.
        cmd.CommandText = sql;
#pragma warning restore CA2100
        return cmd;
    }

    private sealed record SecretSample(string Id, string Usage, int Version, SecretCipher.Sealed Sealed);

    /// <summary>Vrai si la clé existe et déchiffre l'échantillon (sans échantillon : la clé doit seulement exister).</summary>
    private static bool Opens(Func<byte[]> getKey, SecretSample? sample)
    {
        byte[]? key = null;
        try
        {
            key = getKey();
            if (sample is not null)
            {
                CryptographicOperations.ZeroMemory(SecretCipher.Decrypt(key, sample.Id, sample.Usage, sample.Version, sample.Sealed));
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or IOException or SecretIntegrityException)
        {
            return false;
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private static async Task<Dictionary<int, SecretSample>> SampleSecretsAsync(string configPath, CancellationToken ct)
    {
        await using var c = OpenFile(configPath, SqliteOpenMode.ReadOnly);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Cmd(c, "SELECT Id, Usage, KeyVersion, Nonce, Ciphertext, Tag FROM Secret ORDER BY Id;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<int, SecretSample>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var v = r.GetInt32(2);
            result.TryAdd(v, new SecretSample(r.GetString(0), r.GetString(1), v, new SecretCipher.Sealed((byte[])r[3], (byte[])r[4], (byte[])r[5])));
        }

        return result;
    }

    private static BackupManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("Archive sans manifeste : ce n'est pas une sauvegarde ERP Connect.");
        if (entry.Length > 64 * 1024)
        {
            throw new InvalidDataException("Manifeste trop volumineux.");
        }

        using var s = entry.Open();
        return JsonSerializer.Deserialize<BackupManifest>(s, Json) ?? throw new InvalidDataException("Manifeste vide.");
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var s = await entry.OpenAsync(ct).ConfigureAwait(false);
        await using var w = new StreamWriter(s);
        await w.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
    }

    /// <summary>Extraction bornée à la taille annoncée par le manifeste (anti-bombe zip).</summary>
    private static async Task ExtractBoundedAsync(ZipArchiveEntry entry, string destination, long expectedSize, CancellationToken ct)
    {
        await using var input = entry.Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > expectedSize)
            {
                throw new InvalidDataException($"{entry.FullName} dépasse la taille annoncée.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        if (total != expectedSize)
        {
            throw new InvalidDataException($"{entry.FullName} incomplet.");
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false));
    }

    private static SqliteConnection OpenFile(string path, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());

    private static async Task<int> SchemaVersionAsync(string path, CancellationToken ct)
    {
        await using var c = OpenFile(path, SqliteOpenMode.ReadOnly);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Cmd(c, "PRAGMA user_version;");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<List<int>> KeyVersionsAsync(string configPath, CancellationToken ct)
    {
        await using var c = OpenFile(configPath, SqliteOpenMode.ReadOnly);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Cmd(c, "SELECT DISTINCT KeyVersion FROM Secret UNION SELECT Version FROM KeyVersion WHERE Active = 1;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<int>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(r.GetInt32(0));
        }

        return list;
    }

    private static async Task<byte[]> KeyEntropyAsync(string configPath, CancellationToken ct)
    {
        await using var c = OpenFile(configPath, SqliteOpenMode.ReadOnly);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Cmd(c, "SELECT KeyEntropy FROM Installation LIMIT 1;");
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as byte[] ?? throw new InvalidDataException("Sauvegarde sans installation.");
    }

    /// <summary>Supprime secrets et versions de clés : le coffre recrée une clé au démarrage ; les connexions gardent leurs paramètres non secrets.</summary>
    private static async Task DiscardSecretsAsync(string configPath, CancellationToken ct)
    {
        await using var c = OpenFile(configPath, SqliteOpenMode.ReadWrite);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Cmd(c, "DELETE FROM Secret; DELETE FROM KeyVersion;");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
