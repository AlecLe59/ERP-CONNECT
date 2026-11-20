using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErpConnect.Security.Licensing;

#pragma warning disable CA1819 // Forme sérialisée.

/// <summary>
/// Contenu signé d'une licence. Liée à une installation (identifiant affiché dans l'écran Licence) : activation hors ligne,
/// sans appel réseau. features : « scheduler », « scripts » ; maxPublishedFlows null = illimité ; expiresAt null = sans fin.
/// </summary>
public sealed record LicensePayload(
    int Format,
    string LicenseId,
    string Customer,
    string Edition,
    string InstallationId,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    string[] Features,
    int? MaxPublishedFlows);

/// <summary>Fichier de licence : charge utile JSON en base64 et signature ECDSA P-256 / SHA-256 (format IEEE P1363) en base64.</summary>
public sealed record LicenseFile(int Format, string Payload, string Signature);

public sealed class LicenseException : Exception
{
    public LicenseException()
    {
    }

    public LicenseException(string message)
        : base(message)
    {
    }

    public LicenseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Vérification des licences avec les clés publiques de l'éditeur (jamais de clé privée dans le moteur).</summary>
public sealed class LicenseVerifier
{
    public const string PublicKeyMetadata = "ErpConnect.LicensePublicKey";
    public const int MaxFileLength = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        MaxDepth = 8,
    };

    private readonly IReadOnlyList<byte[]> _publicKeys;

    /// <param name="publicKeys">Clés publiques SubjectPublicKeyInfo (DER).</param>
    public LicenseVerifier(IEnumerable<byte[]> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        _publicKeys = publicKeys.ToList();
        foreach (var k in _publicKeys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(k, out _);
            if (ecdsa.KeySize != 256)
            {
                throw new ArgumentException("Clé ECDSA P-256 attendue.", nameof(publicKeys));
            }
        }
    }

    public bool HasTrustedKey => _publicKeys.Count > 0;

    /// <summary>Clé de l'éditeur fournie à la compilation (propriété MSBuild ErpConnectLicensePublicKey, base64). Absente : aucune licence vérifiable.</summary>
    public static LicenseVerifier FromBuild()
    {
        var value = typeof(LicenseVerifier).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == PublicKeyMetadata)?.Value;
        return new LicenseVerifier(string.IsNullOrWhiteSpace(value) ? [] : [Convert.FromBase64String(value)]);
    }

    public LicensePayload Verify(string fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent) || fileContent.Length > MaxFileLength)
        {
            throw new LicenseException("Fichier de licence vide ou trop volumineux.");
        }

        if (!HasTrustedKey)
        {
            throw new LicenseException("Cette version ne contient aucune clé d'éditeur : les licences ne peuvent pas être vérifiées.");
        }

        LicenseFile file;
        byte[] payload, signature;
        try
        {
            file = JsonSerializer.Deserialize<LicenseFile>(fileContent, Json) ?? throw new LicenseException("Fichier de licence vide.");
            payload = Convert.FromBase64String(file.Payload);
            signature = Convert.FromBase64String(file.Signature);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new LicenseException("Fichier de licence illisible.", ex);
        }

        if (file.Format != 1)
        {
            throw new LicenseException("Format de licence non pris en charge.");
        }

        var valid = false;
        foreach (var key in _publicKeys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out _);
            if (ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                valid = true;
                break;
            }
        }

        if (!valid)
        {
            throw new LicenseException("Signature de licence invalide.");
        }

        LicensePayload content;
        try
        {
            content = JsonSerializer.Deserialize<LicensePayload>(payload, Json) ?? throw new LicenseException("Licence vide.");
        }
        catch (JsonException ex)
        {
            throw new LicenseException("Contenu de licence illisible.", ex);
        }

        if (content.Format != 1 || string.IsNullOrWhiteSpace(content.LicenseId) || string.IsNullOrWhiteSpace(content.InstallationId))
        {
            throw new LicenseException("Contenu de licence incomplet.");
        }

        return content;
    }

    /// <summary>Signature d'une licence (outil de l'éditeur uniquement, clé privée PKCS#8 hors du moteur).</summary>
    public static string Sign(LicensePayload payload, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(privateKey);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json));
        var signature = privateKey.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.Serialize(new LicenseFile(1, Convert.ToBase64String(bytes), Convert.ToBase64String(signature)), Json);
    }
}
