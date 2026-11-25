using System.Globalization;
using ErpConnect.Core.Flows;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage.Repositories;

public sealed class FlowRepository : IFlowRepository
{
    private readonly SqliteDatabase _db;

    public FlowRepository(SqliteDatabase configDb) => _db = configDb;

    private const string Columns = "Id, Name, Description, DraftVersion, PublishedVersion, UpdatedAt, UpdatedBy";

    private static FlowRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4),
        DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture), r.GetString(6));

    private static string Iso(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<FlowRecord>> ListAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command($"SELECT {Columns} FROM Flow ORDER BY Name;");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<FlowRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Read(r));
        }

        return list;
    }

    public async Task<FlowRecord?> GetAsync(string id, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await GetAsync(c, null, id, ct).ConfigureAwait(false);
    }

    private static async Task<FlowRecord?> GetAsync(SqliteConnection c, SqliteTransaction? tx, string id, CancellationToken ct)
    {
        await using var cmd = c.Command($"SELECT {Columns} FROM Flow WHERE Id = $id;", tx).With("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task<FlowVersionRecord?> GetVersionAsync(string flowId, long version, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "SELECT FlowId, Version, DefinitionJson, CreatedAt, CreatedBy, PublishedAt, PublishedBy FROM FlowVersion WHERE FlowId = $f AND Version = $v;")
            .With("$f", flowId).With("$v", version);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new FlowVersionRecord(r.GetString(0), r.GetInt64(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture), r.GetString(4),
            r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture), r.IsDBNull(6) ? null : r.GetString(6));
    }

    public async Task<FlowRecord?> SaveAsync(string id, string name, string description, string definitionJson, long expectedDraftVersion, string by, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var version = expectedDraftVersion + 1;
        try
        {
            if (expectedDraftVersion == 0)
            {
                await using var ins = c.Command("INSERT INTO Flow (Id, Name, Description, DraftVersion, UpdatedAt, UpdatedBy) VALUES ($id, $n, $d, 1, $at, $by);", tx)
                    .With("$id", id).With("$n", name).With("$d", description).With("$at", Iso(at)).With("$by", by);
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await using var upd = c.Command("UPDATE Flow SET Name = $n, Description = $d, DraftVersion = $v, UpdatedAt = $at, UpdatedBy = $by WHERE Id = $id AND DraftVersion = $e;", tx)
                    .With("$id", id).With("$n", name).With("$d", description).With("$v", version).With("$at", Iso(at)).With("$by", by).With("$e", expectedDraftVersion);
                if (await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                {
                    return null;
                }
            }

            await using var v = c.Command("INSERT INTO FlowVersion (FlowId, Version, DefinitionJson, CreatedAt, CreatedBy) VALUES ($id, $v, $j, $at, $by);", tx)
                .With("$id", id).With("$v", version).With("$j", definitionJson).With("$at", Iso(at)).With("$by", by);
            await v.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return null;
        }

        var result = await GetAsync(c, tx, id, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<FlowRecord?> PublishAsync(string flowId, long version, string by, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var v = c.Command("UPDATE FlowVersion SET PublishedAt = $at, PublishedBy = $by WHERE FlowId = $f AND Version = $v AND PublishedAt IS NULL;", tx)
            .With("$f", flowId).With("$v", version).With("$at", Iso(at)).With("$by", by))
        {
            await v.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var f = c.Command(
            "UPDATE Flow SET PublishedVersion = $v, UpdatedAt = $at, UpdatedBy = $by WHERE Id = $f AND EXISTS (SELECT 1 FROM FlowVersion WHERE FlowId = $f AND Version = $v);", tx)
            .With("$f", flowId).With("$v", version).With("$at", Iso(at)).With("$by", by))
        {
            if (await f.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return null;
            }
        }

        var result = await GetAsync(c, tx, flowId, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<string>> FlowsUsingConnectionAsync(string connectionId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            """
            SELECT f.Name FROM Flow f JOIN FlowVersion v ON v.FlowId = f.Id AND v.Version IN (f.DraftVersion, f.PublishedVersion)
            WHERE json_extract(v.DefinitionJson, '$.source.connectionId') = $c OR json_extract(v.DefinitionJson, '$.target.connectionId') = $c
            GROUP BY f.Name ORDER BY f.Name;
            """).With("$c", connectionId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<string>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(r.GetString(0));
        }

        return list;
    }
}
