using System.Globalization;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Authorization;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage.Repositories;

public sealed class RoleAssignmentRepository : IRoleAssignmentRepository
{
    private readonly SqliteDatabase _db;

    public RoleAssignmentRepository(SqliteDatabase configDb) => _db = configDb;

    public async Task<IReadOnlyList<RoleAssignment>> ListAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            """
            SELECT a.Id, a.PrincipalSid, p.Name, p.Kind, a.Role, a.ScopeKind, a.ScopeId, a.CreatedAt, a.CreatedBy
            FROM RoleAssignment a JOIN Principal p ON p.Sid = a.PrincipalSid
            ORDER BY a.Id;
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RoleAssignment>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var scopeId = r.GetString(6);
            list.Add(new RoleAssignment(
                r.GetInt64(0), r.GetString(1), r.GetString(2), (PrincipalKind)r.GetInt32(3), (Role)r.GetInt32(4),
                (ScopeKind)r.GetInt32(5), scopeId.Length == 0 ? null : scopeId,
                DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture), r.GetString(8)));
        }

        return list;
    }

    /// <summary>Ajoute l'attribution ; si elle existe déjà, renvoie l'existante.</summary>
    public async Task<RoleAssignment> AddAsync(RoleAssignment assignment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var at = assignment.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
        await using (var p = c.Command(
            """
            INSERT INTO Principal (Sid, Name, Kind, UpdatedAt) VALUES ($sid, $name, $kind, $at)
            ON CONFLICT (Sid) DO UPDATE SET Name = excluded.Name, Kind = excluded.Kind, UpdatedAt = excluded.UpdatedAt;
            """, tx).With("$sid", assignment.PrincipalSid).With("$name", assignment.PrincipalName)
            .With("$kind", (int)assignment.PrincipalKind).With("$at", at))
        {
            await p.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var a = c.Command(
            """
            INSERT OR IGNORE INTO RoleAssignment (PrincipalSid, Role, ScopeKind, ScopeId, CreatedAt, CreatedBy)
            VALUES ($sid, $role, $sk, $sid2, $at, $by);
            """, tx).With("$sid", assignment.PrincipalSid).With("$role", (int)assignment.Role).With("$sk", (int)assignment.ScopeKind)
            .With("$sid2", assignment.ScopeId ?? string.Empty).With("$at", at).With("$by", assignment.CreatedBy))
        {
            await a.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        long id;
        await using (var q = c.Command(
            "SELECT Id FROM RoleAssignment WHERE PrincipalSid = $sid AND Role = $role AND ScopeKind = $sk AND ScopeId = $sid2;", tx)
            .With("$sid", assignment.PrincipalSid).With("$role", (int)assignment.Role).With("$sk", (int)assignment.ScopeKind)
            .With("$sid2", assignment.ScopeId ?? string.Empty))
        {
            id = Convert.ToInt64(await q.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (await ListAsync(ct).ConfigureAwait(false)).Single(x => x.Id == id);
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("DELETE FROM RoleAssignment WHERE Id = $id;").With("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}
