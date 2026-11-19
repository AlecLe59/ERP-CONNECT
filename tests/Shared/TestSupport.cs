using System.Security.Cryptography;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Authorization;
using ErpConnect.Security.Vault;

namespace ErpConnect.Tests.Shared;

/// <summary>
/// Protecteur de clé POUR LES TESTS UNIQUEMENT : enveloppe AES-GCM avec une clé fixe en mémoire.
/// Il n'existe dans aucun projet de production. La protection réelle est DPAPI (Windows).
/// </summary>
internal sealed class TestKeyProtector : IKeyProtector
{
    private static readonly byte[] WrapKey = SHA256.HashData("erpconnect-tests-only"u8);

    public string Description => "Protecteur de test (non sécurisé)";

    public byte[] Protect(byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[key.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(WrapKey, 16);
        aes.Encrypt(nonce, key, ct, tag);
        return [.. nonce, .. tag, .. ct];
    }

    public byte[] Unprotect(byte[] blob)
    {
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(WrapKey, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
        return plain;
    }
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Répertoire temporaire supprimé à la fin du test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "erpconnect-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class FakePrincipalResolver : IPrincipalResolver
{
    public Dictionary<string, ResolvedPrincipal> Known { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ResolvedPrincipal? Resolve(string name) => Known.TryGetValue(name, out var p) ? p : null;
}

internal static class Identities
{
    public const string AdminsGroup = WellKnownSids.BuiltinAdministrators;
    public const string OperatorsGroup = "S-1-5-21-1000-1000-1000-2001";

    public static CallerIdentity Admin { get; } = new("S-1-5-21-1000-1000-1000-1001", @"PC\admin", [AdminsGroup]);

    public static CallerIdentity NoRole { get; } = new("S-1-5-21-1000-1000-1000-1099", @"PC\nobody", ["S-1-5-32-545"]);

    public static CallerIdentity ForUser(string rid) => new($"S-1-5-21-1000-1000-1000-{rid}", $@"PC\user{rid}", ["S-1-5-32-545"]);
}
