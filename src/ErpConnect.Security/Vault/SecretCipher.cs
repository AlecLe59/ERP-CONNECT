using System.Security.Cryptography;
using System.Text;

namespace ErpConnect.Security.Vault;

/// <summary>AES-256-GCM, nonce aléatoire de 12 octets, étiquette de 16 octets (ADR 0007).</summary>
public static class SecretCipher
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public sealed record Sealed(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

    /// <summary>
    /// Données associées : identifiant, usage et version de clé. Un secret recopié sur une autre ligne,
    /// ou présenté avec un autre usage, ne se déchiffre pas.
    /// </summary>
    public static byte[] AssociatedData(string id, string usage, int keyVersion) =>
        Encoding.UTF8.GetBytes($"erpconnect.secret.v1\n{id}\n{usage}\n{keyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

    public static Sealed Encrypt(ReadOnlySpan<byte> key, string id, string usage, int keyVersion, ReadOnlySpan<byte> plaintext)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("La clé doit faire 32 octets.", nameof(key));
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(id, usage, keyVersion));
        return new Sealed(nonce, ciphertext, tag);
    }

    /// <summary>Déchiffre ; lève <see cref="SecretIntegrityException"/> si la donnée a été altérée ou déplacée.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> key, string id, string usage, int keyVersion, Sealed sealedSecret)
    {
        ArgumentNullException.ThrowIfNull(sealedSecret);
        if (key.Length != KeySize)
        {
            throw new ArgumentException("La clé doit faire 32 octets.", nameof(key));
        }

        if (sealedSecret.Nonce.Length != NonceSize || sealedSecret.Tag.Length != TagSize)
        {
            throw new SecretIntegrityException(id);
        }

        var plaintext = new byte[sealedSecret.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(sealedSecret.Nonce, sealedSecret.Ciphertext, sealedSecret.Tag, plaintext, AssociatedData(id, usage, keyVersion));
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SecretIntegrityException(id);
        }
    }
}

/// <summary>Le secret a été modifié, déplacé, ou la clé ne correspond pas. Le message ne contient jamais le secret.</summary>
public sealed class SecretIntegrityException : Exception
{
    public SecretIntegrityException(string secretId)
        : base($"Le secret « {secretId} » ne peut pas être déchiffré : donnée altérée, déplacée ou clé incorrecte.")
    {
        SecretId = secretId;
    }

    public SecretIntegrityException()
        : base("Secret illisible.")
    {
        SecretId = string.Empty;
    }

    public SecretIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
        SecretId = string.Empty;
    }

    public string SecretId { get; }
}
