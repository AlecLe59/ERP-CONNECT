using System.Globalization;
using System.Security.Cryptography;

namespace ErpConnect.Security.Vault;

/// <summary>
/// Clés maîtres versionnées, stockées protégées dans keys\master.v{n}.key.
/// Les droits du dossier keys sont posés par l'installateur (service + SYSTEM uniquement).
/// </summary>
public sealed class MasterKeyStore : IDisposable
{
    private readonly string _directory;
    private readonly IKeyProtector _protector;
    private readonly Dictionary<int, byte[]> _cache = [];
    private readonly Lock _lock = new();

    public MasterKeyStore(string keysDirectory, IKeyProtector protector)
    {
        _directory = keysDirectory;
        _protector = protector;
    }

    public string ProtectionDescription => _protector.Description;

    public IReadOnlyList<int> ExistingVersions()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var versions = new List<int>();
        foreach (var file in Directory.EnumerateFiles(_directory, "master.v*.key"))
        {
            var name = Path.GetFileNameWithoutExtension(file); // master.vN
            if (int.TryParse(name.AsSpan("master.v".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var v))
            {
                versions.Add(v);
            }
        }

        versions.Sort();
        return versions;
    }

    /// <summary>Crée une nouvelle clé de 32 octets aléatoires sous la version indiquée. Échoue si elle existe.</summary>
    public void Create(int version)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(version);
        var key = RandomNumberGenerator.GetBytes(SecretCipher.KeySize);
        try
        {
            var blob = _protector.Protect(key);
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(blob);
                fs.Flush(flushToDisk: true);
            }

            lock (_lock)
            {
                _cache[version] = (byte[])key.Clone();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Renvoie une copie de la clé ; l'appelant l'efface après usage.</summary>
    public byte[] Get(int version)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(version, out var key))
            {
                var path = PathFor(version);
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException($"Clé maître v{version} absente.");
                }

                key = _protector.Unprotect(File.ReadAllBytes(path));
                if (key.Length != SecretCipher.KeySize)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new InvalidOperationException($"Clé maître v{version} invalide.");
                }

                _cache[version] = key;
            }

            return (byte[])key.Clone();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var k in _cache.Values)
            {
                CryptographicOperations.ZeroMemory(k);
            }

            _cache.Clear();
        }
    }

    private string PathFor(int version) =>
        Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"master.v{version}.key"));
}
