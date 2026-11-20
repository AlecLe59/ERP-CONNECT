using System.Globalization;
using System.Security.Cryptography;

namespace ErpConnect.Storage.Repositories;

public sealed record InstallationRecord(string Id, DateTimeOffset InstalledAt, string MachineName, byte[] KeyEntropy);

public sealed class InstallationRepository
{
    private readonly SqliteDatabase _db;
    private readonly TimeProvider _time;

    public InstallationRepository(SqliteDatabase configDb, TimeProvider? time = null)
    {
        _db = configDb;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Renvoie l'installation ; la crée au premier démarrage (identifiant et entropie DPAPI aléatoires).</summary>
    public async Task<InstallationRecord> GetOrCreateAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using (var insert = c.Command(
            "INSERT INTO Installation (Id, InstalledAt, MachineName, KeyEntropy) SELECT $id, $at, $m, $e WHERE NOT EXISTS (SELECT 1 FROM Installation);")
            .With("$id", Guid.NewGuid().ToString("D")).With("$at", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture))
            .With("$m", Environment.MachineName).With("$e", RandomNumberGenerator.GetBytes(32)))
        {
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = c.Command("SELECT Id, InstalledAt, MachineName, KeyEntropy FROM Installation LIMIT 1;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        return new InstallationRecord(r.GetString(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2), (byte[])r[3]);
    }
}
