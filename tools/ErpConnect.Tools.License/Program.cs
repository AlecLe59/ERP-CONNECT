using System.Globalization;
using System.Security.Cryptography;
using ErpConnect.Security.Licensing;

// Outil de l'ÉDITEUR, jamais livré aux clients. La clé privée reste hors du dépôt (coffre de l'éditeur, HSM idéalement).
//   keygen <fichier-clé-privée.pem>              crée une paire P-256 ; affiche la clé publique à passer à la compilation
//   issue <clé.pem> <installationId> <client> <édition> <expire:yyyy-MM-dd|never> <fonctions,séparées> <maxFlux|unlimited> <sortie.lic>
//   verify <clé-publique-base64> <fichier.lic>
return args switch
{
    ["keygen", var keyPath] => KeyGen(keyPath),
    ["issue", var keyPath, var installation, var customer, var edition, var expires, var features, var maxFlows, var output] =>
        Issue(keyPath, installation, customer, edition, expires, features, maxFlows, output),
    ["verify", var publicKey, var file] => Verify(publicKey, file),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage : keygen <clé.pem> | issue <clé.pem> <installationId> <client> <édition> <yyyy-MM-dd|never> <fonctions> <maxFlux|unlimited> <sortie.lic> | verify <clé-publique-base64> <fichier.lic>");
    return 2;
}

static int KeyGen(string keyPath)
{
    if (File.Exists(keyPath))
    {
        Console.Error.WriteLine("Le fichier existe déjà : refus d'écraser une clé.");
        return 1;
    }

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    Console.WriteLine("Clé privée écrite. À conserver hors du dépôt. Clé publique (ErpConnectLicensePublicKey) :");
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

static int Issue(string keyPath, string installation, string customer, string edition, string expires, string features, string maxFlows, string output)
{
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(keyPath));
    var payload = new LicensePayload(
        1, Guid.NewGuid().ToString("N"), customer, edition, installation, DateTimeOffset.UtcNow,
        expires == "never" ? null : DateTimeOffset.ParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        features.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        maxFlows == "unlimited" ? null : int.Parse(maxFlows, CultureInfo.InvariantCulture));
    File.WriteAllText(output, LicenseVerifier.Sign(payload, key));
    Console.WriteLine($"Licence {payload.LicenseId} écrite pour l'installation {installation}.");
    return 0;
}

static int Verify(string publicKey, string file)
{
    try
    {
        var p = new LicenseVerifier([Convert.FromBase64String(publicKey)]).Verify(File.ReadAllText(file));
        Console.WriteLine($"Valide : {p.LicenseId}, {p.Customer}, {p.Edition}, installation {p.InstallationId}, expire {p.ExpiresAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "jamais"}.");
        return 0;
    }
    catch (LicenseException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}
