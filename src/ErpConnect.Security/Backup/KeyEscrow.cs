using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace ErpConnect.Security.Backup;

/// <summary>Paramètres Argon2id enregistrés avec l'export (relus et bornés à l'import).</summary>
public sealed record Argon2Parameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>64 Mio, 3 passes, 2 voies (recommandation OWASP 2024 pour Argon2id : au moins 19 Mio et 2 passes).</summary>
    public static Argon2Parameters Default { get; } = new(64 * 1024, 3, 2);

    /// <summary>Paramètres faibles réservés aux tests (dérivation rapide).</summary>
    public static Argon2Parameters ForTests { get; } = new(8 * 1024, 1, 1);

    public bool WithinBounds => MemoryKiB is >= 8 * 1024 and <= 1024 * 1024 && Iterations is >= 1 and <= 10 && Parallelism is >= 1 and <= 16;
}

/// <summary>Fichier keys.json d'une sauvegarde : clés maîtres chiffrées par une phrase de passe.</summary>
public sealed record KeyEscrowFile(int Format, string Kdf, int MemoryKiB, int Iterations, int Parallelism, string Salt, string Nonce, string Ciphertext, string Tag);

/// <summary>
/// Export des clés maîtres pour restaurer les secrets sur une autre machine (DPAPI ne le permet pas).
/// Clé de chiffrement = Argon2id(phrase, sel aléatoire 16 octets) ; chiffrement AES-256-GCM, données associées = en-tête de l'export.
/// La phrase de passe n'est jamais stockée ; sans elle, l'export est inutilisable.
/// </summary>
public static class KeyEscrow
{
    public const int MinimumPassphraseLength = 12;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    public static string Export(IReadOnlyDictionary<int, byte[]> keys, string passphrase, Argon2Parameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        CheckPassphrase(passphrase);
        parameters ??= Argon2Parameters.Default;
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = JsonSerializer.SerializeToUtf8Bytes(keys.ToDictionary(k => k.Key.ToString(CultureInfo.InvariantCulture), k => Convert.ToBase64String(k.Value)), Json);
        var kek = Derive(passphrase, salt, parameters);
        try
        {
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var gcm = new AesGcm(kek, 16))
            {
                gcm.Encrypt(nonce, plain, cipher, tag, Header(parameters, salt));
            }

            return JsonSerializer.Serialize(new KeyEscrowFile(1, "argon2id", parameters.MemoryKiB, parameters.Iterations, parameters.Parallelism,
                Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag)), Json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Renvoie les clés (l'appelant les efface après usage). Lève CryptographicException si la phrase est fausse ou le fichier altéré.</summary>
    public static Dictionary<int, byte[]> Import(string content, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        KeyEscrowFile file;
        byte[] salt, nonce, cipher, tag;
        try
        {
            file = JsonSerializer.Deserialize<KeyEscrowFile>(content, Json) ?? throw new CryptographicException("Export de clés vide.");
            salt = Convert.FromBase64String(file.Salt);
            nonce = Convert.FromBase64String(file.Nonce);
            cipher = Convert.FromBase64String(file.Ciphertext);
            tag = Convert.FromBase64String(file.Tag);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentNullException)
        {
            throw new CryptographicException("Export de clés illisible.", ex);
        }

        var parameters = new Argon2Parameters(file.MemoryKiB, file.Iterations, file.Parallelism);
        if (file.Format != 1 || file.Kdf != "argon2id" || !parameters.WithinBounds || salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || cipher.Length > 64 * 1024)
        {
            throw new CryptographicException("Export de clés non pris en charge ou paramètres hors bornes.");
        }

        var kek = Derive(passphrase, salt, parameters);
        var plain = new byte[cipher.Length];
        try
        {
            using (var gcm = new AesGcm(kek, 16))
            {
                gcm.Decrypt(nonce, cipher, tag, plain, Header(parameters, salt));
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(plain, Json) ?? [];
            var result = new Dictionary<int, byte[]>();
            foreach (var (version, key) in map)
            {
                var bytes = Convert.FromBase64String(key);
                if (bytes.Length != 32 || !int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var v) || v < 1)
                {
                    throw new CryptographicException("Clé exportée invalide.");
                }

                result[v] = bytes;
            }

            return result;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new CryptographicException("Phrase de passe incorrecte ou export altéré.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void CheckPassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < MinimumPassphraseLength)
        {
            throw new ArgumentException($"Phrase de passe de {MinimumPassphraseLength} caractères minimum.", nameof(passphrase));
        }
    }

    private static byte[] Header(Argon2Parameters p, byte[] salt) =>
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"ERPConnect.KeyEscrow.v1|argon2id|{p.MemoryKiB}|{p.Iterations}|{p.Parallelism}|{Convert.ToBase64String(salt)}"));

    private static byte[] Derive(string passphrase, byte[] salt, Argon2Parameters p)
    {
        var password = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            using var argon = new Argon2id(password) { Salt = salt, MemorySize = p.MemoryKiB, Iterations = p.Iterations, DegreeOfParallelism = p.Parallelism };
            return argon.GetBytes(32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }
}
