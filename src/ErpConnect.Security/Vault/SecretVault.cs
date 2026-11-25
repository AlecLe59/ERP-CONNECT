using System.Security.Cryptography;
using ErpConnect.Core.Abstractions;

namespace ErpConnect.Security.Vault;

/// <summary>
/// Coffre de secrets. Store et Resolve n'existent que dans le service : aucune méthode RPC ne renvoie un secret.
/// </summary>
public sealed class SecretVault : ISecretVault, IDisposable
{
    private readonly ISecretStore _store;
    private readonly MasterKeyStore _keys;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int? _activeVersion;

    public SecretVault(ISecretStore store, MasterKeyStore keys, IClock clock)
    {
        _store = store;
        _keys = keys;
        _clock = clock;
    }

    public bool IsInitialized => _activeVersion is not null;

    public int? ActiveKeyVersion => _activeVersion;

    public string ProtectionDescription => _keys.ProtectionDescription;

    /// <summary>Au premier démarrage, crée la clé maître v1 ; ensuite vérifie qu'elle est lisible.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var active = await _store.GetActiveKeyVersionAsync(ct).ConfigureAwait(false);
            if (active is null)
            {
                if (_keys.ExistingVersions().Count > 0)
                {
                    throw new InvalidOperationException(
                        "Des clés maîtres existent sur disque mais la base n'en référence aucune : restauration incohérente.");
                }

                _keys.Create(1);
                await _store.RegisterKeyVersionAsync(1, ct).ConfigureAwait(false);
                active = 1;
            }

            var probe = _keys.Get(active.Value); // vérifie que DPAPI déprotège bien la clé
            CryptographicOperations.ZeroMemory(probe);
            _activeVersion = active;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StoreAsync(string id, string usage, ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(usage);
        var version = RequireActive();
        var key = _keys.Get(version);
        try
        {
            var sealedSecret = SecretCipher.Encrypt(key, id, usage, version, plaintext.Span);
            var existing = await _store.GetAsync(id, ct).ConfigureAwait(false);
            var now = _clock.UtcNow;
            await _store.UpsertAsync(
                new SecretRecord(id, usage, version, sealedSecret.Nonce, sealedSecret.Ciphertext, sealedSecret.Tag, existing?.CreatedAt ?? now, now),
                ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<byte[]?> ResolveAsync(string id, string usage, CancellationToken ct)
    {
        var record = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var key = _keys.Get(record.KeyVersion);
        try
        {
            // L'usage demandé (et non celui stocké) entre dans les données associées : un détournement d'usage échoue.
            return SecretCipher.Decrypt(key, record.Id, usage, record.KeyVersion,
                new SecretCipher.Sealed(record.Nonce, record.Ciphertext, record.Tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct) => _store.DeleteAsync(id, ct);

    /// <summary>Nouvelle version de clé, rechiffrement de tous les secrets en une transaction.</summary>
    public async Task RotateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = RequireActive();
            var next = Math.Max(current, _keys.ExistingVersions().DefaultIfEmpty(0).Max()) + 1;
            _keys.Create(next);
            var newKey = _keys.Get(next);
            try
            {
                var all = await _store.ListAllAsync(ct).ConfigureAwait(false);
                var rewrapped = new List<SecretRecord>(all.Count);
                foreach (var r in all)
                {
                    var oldKey = _keys.Get(r.KeyVersion);
                    byte[]? plain = null;
                    try
                    {
                        plain = SecretCipher.Decrypt(oldKey, r.Id, r.Usage, r.KeyVersion, new SecretCipher.Sealed(r.Nonce, r.Ciphertext, r.Tag));
                        var s = SecretCipher.Encrypt(newKey, r.Id, r.Usage, next, plain);
                        rewrapped.Add(r with { KeyVersion = next, Nonce = s.Nonce, Ciphertext = s.Ciphertext, Tag = s.Tag, UpdatedAt = _clock.UtcNow });
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(oldKey);
                        if (plain is not null)
                        {
                            CryptographicOperations.ZeroMemory(plain);
                        }
                    }
                }

                await _store.ReplaceAllAsync(rewrapped, next, ct).ConfigureAwait(false);
                _activeVersion = next;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(newKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private int RequireActive() => _activeVersion ?? throw new InvalidOperationException("Coffre non initialisé.");

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 200 || id.Any(c => char.IsControl(c)))
        {
            throw new ArgumentException("Identifiant de secret invalide.", nameof(id));
        }
    }
}
