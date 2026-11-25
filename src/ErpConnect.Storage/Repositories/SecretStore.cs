using System.Globalization;
using ErpConnect.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage.Repositories;

/// <summary>Stockage des secrets chiffrés (aucun texte clair ne transite par cette classe).</summary>
public sealed class SecretStore : ISecretStore
{
    private readonly SqliteDatabase _db;
    private readonly TimeProvider _time;

    public SecretStore(SqliteDatabase configDb, TimeProvider? time = null)
    {
        _db = configDb;
        _time = time ?? TimeProvider.System;
    }

    public async Task<SecretRecord?> GetAsync(string id, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "SELECT Id, Usage, KeyVersion, Nonce, Ciphertext, Tag, CreatedAt, UpdatedAt FROM Secret WHERE Id = $id;").With("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task UpsertAsync(SecretRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Upsert(c, null, record);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("DELETE FROM Secret WHERE Id = $id;").With("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<SecretRecord>> ListAllAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("SELECT Id, Usage, KeyVersion, Nonce, Ciphertext, Tag, CreatedAt, UpdatedAt FROM Secret ORDER BY Id;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<SecretRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Read(r));
        }

        return list;
    }

    public async Task ReplaceAllAsync(IReadOnlyList<SecretRecord> records, int newActiveKeyVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RegisterKeyVersionCoreAsync(c, tx, newActiveKeyVersion, ct).ConfigureAwait(false);
        foreach (var r in records)
        {
            await using var cmd = Upsert(c, tx, r);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> GetActiveKeyVersionAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("SELECT Version FROM KeyVersion WHERE Active = 1;");
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);
    }

    public async Task RegisterKeyVersionAsync(int version, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RegisterKeyVersionCoreAsync(c, tx, version, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("SELECT COUNT(*) FROM Secret;");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task RegisterKeyVersionCoreAsync(SqliteConnection c, SqliteTransaction tx, int version, CancellationToken ct)
    {
        await using (var off = c.Command("UPDATE KeyVersion SET Active = 0 WHERE Active = 1;", tx))
        {
            await off.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var on = c.Command(
            "INSERT INTO KeyVersion (Version, CreatedAt, Active) VALUES ($v, $at, 1) ON CONFLICT (Version) DO UPDATE SET Active = 1;", tx)
            .With("$v", version).With("$at", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await on.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static SqliteCommand Upsert(SqliteConnection c, SqliteTransaction? tx, SecretRecord r) =>
        c.Command(
            """
            INSERT INTO Secret (Id, Usage, KeyVersion, Nonce, Ciphertext, Tag, CreatedAt, UpdatedAt)
            VALUES ($id, $usage, $kv, $nonce, $ct, $tag, $created, $updated)
            ON CONFLICT (Id) DO UPDATE SET Usage = excluded.Usage, KeyVersion = excluded.KeyVersion, Nonce = excluded.Nonce,
                Ciphertext = excluded.Ciphertext, Tag = excluded.Tag, UpdatedAt = excluded.UpdatedAt;
            """, tx).With("$id", r.Id).With("$usage", r.Usage).With("$kv", r.KeyVersion).With("$nonce", r.Nonce)
            .With("$ct", r.Ciphertext).With("$tag", r.Tag)
            .With("$created", r.CreatedAt.ToString("O", CultureInfo.InvariantCulture))
            .With("$updated", r.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

    private static SecretRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt32(2), (byte[])r[3], (byte[])r[4], (byte[])r[5],
        DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture));
}
