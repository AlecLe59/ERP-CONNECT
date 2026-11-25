using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace ErpConnect.Security.Vault;

/// <summary>Protège la clé maître au repos.</summary>
public interface IKeyProtector
{
    /// <summary>Description affichée dans vault.getStatus (jamais de donnée sensible).</summary>
    string Description { get; }

    byte[] Protect(byte[] key);

    byte[] Unprotect(byte[] blob);
}

/// <summary>
/// DPAPI dans le contexte du compte du service (CurrentUser), ou machine en repli, avec une entropie propre à
/// l'installation. Le choix définitif dépend du test DPAPI sur compte virtuel (voir docs/phases).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    private readonly byte[] _entropy;
    private readonly DataProtectionScope _scope;

    public DpapiKeyProtector(byte[] installationEntropy, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        ArgumentNullException.ThrowIfNull(installationEntropy);
        if (installationEntropy.Length < 16)
        {
            throw new ArgumentException("Entropie trop courte.", nameof(installationEntropy));
        }

        _entropy = (byte[])installationEntropy.Clone();
        _scope = scope;
    }

    public string Description => _scope == DataProtectionScope.CurrentUser ? "DPAPI (compte du service)" : "DPAPI (machine)";

    public byte[] Protect(byte[] key) => ProtectedData.Protect(key, _entropy, _scope);

    public byte[] Unprotect(byte[] blob)
    {
        try
        {
            return ProtectedData.Unprotect(blob, _entropy, _scope);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("La clé maître ne peut pas être déprotégée par DPAPI sur cette machine ou ce compte.", ex);
        }
    }
}
