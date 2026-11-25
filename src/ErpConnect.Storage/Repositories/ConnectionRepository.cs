using System.Globalization;
using System.Text.Json;
using ErpConnect.Core.Flows;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage.Repositories;

public sealed class ConnectionRepository : IConnectionRepository
{
    private readonly SqliteDatabase _db;

    public ConnectionRepository(SqliteDatabase configDb) => _db = configDb;

    private const string Columns = "Id, Name, ConnectorId, SettingsJson, SecretFields, Version, UpdatedAt, UpdatedBy";

    private static ConnectionRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(3)) ?? [],
        r.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries),
        r.GetInt64(5), DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), r.GetString(7));

    public async Task<IReadOnlyList<ConnectionRecord>> ListAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command($"SELECT {Columns} FROM Connection ORDER BY Name;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ConnectionRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Read(r));
        }

        return list;
    }

    public async Task<ConnectionRecord?> GetAsync(string id, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command($"SELECT {Columns} FROM Connection WHERE Id = $id;").With("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task<ConnectionRecord?> SaveAsync(ConnectionRecord record, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        var settings = JsonSerializer.Serialize(record.Settings);
        var secrets = string.Join(',', record.SecretFields);
        var at = record.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
        int changed;
        try
        {
            if (expectedVersion == 0)
            {
                await using var ins = c.Command(
                    "INSERT INTO Connection (Id, Name, ConnectorId, SettingsJson, SecretFields, Version, UpdatedAt, UpdatedBy) VALUES ($id, $n, $c, $s, $sf, 1, $at, $by);")
                    .With("$id", record.Id).With("$n", record.Name).With("$c", record.ConnectorId).With("$s", settings).With("$sf", secrets).With("$at", at).With("$by", record.UpdatedBy);
                changed = await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await using var upd = c.Command(
                    "UPDATE Connection SET Name = $n, SettingsJson = $s, SecretFields = $sf, Version = Version + 1, UpdatedAt = $at, UpdatedBy = $by WHERE Id = $id AND Version = $v;")
                    .With("$id", record.Id).With("$n", record.Name).With("$s", settings).With("$sf", secrets).With("$at", at).With("$by", record.UpdatedBy).With("$v", expectedVersion);
                changed = await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return null; // nom ou identifiant déjà utilisé
        }

        return changed == 1 ? await GetAsync(record.Id, ct).ConfigureAwait(false) : null;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("DELETE FROM Connection WHERE Id = $id;").With("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}
