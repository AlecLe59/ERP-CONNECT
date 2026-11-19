using System.Globalization;
using System.Text;
using ErpConnect.Core.Audit;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage.Repositories;

/// <summary>
/// Accès brut à audit.db. Le calcul des empreintes est fourni par l'appelant (ErpConnect.Security) :
/// le stockage ne dépend que de Core.
/// </summary>
public sealed class AuditRepository
{
    private readonly SqliteDatabase _db;

    public AuditRepository(SqliteDatabase auditDb) => _db = auditDb;

    /// <summary>Lit la dernière entrée et insère la suivante dans la même transaction (écriture immédiate).</summary>
    public async Task<AuditEvent> AppendAsync(
        AuditEntry entry, DateTimeOffset timestamp, Func<string, long, string> computeHash, string genesisHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(computeHash);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using (var begin = c.Command("BEGIN IMMEDIATE;"))
        {
            await begin.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        try
        {
            long last = 0;
            var previous = genesisHash;
            await using (var q = c.Command("SELECT Sequence, Hash FROM AuditEvent ORDER BY Sequence DESC LIMIT 1;"))
            await using (var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    last = r.GetInt64(0);
                    previous = r.GetString(1);
                }
            }

            var sequence = last + 1;
            var hash = computeHash(previous, sequence);
            await using (var ins = c.Command(
                """
                INSERT INTO AuditEvent (Sequence, Timestamp, ActorSid, ActorName, Action, Target, Outcome, Details, CorrelationId, PreviousHash, Hash)
                VALUES ($seq, $ts, $sid, $name, $action, $target, $outcome, $details, $corr, $prev, $hash);
                """).With("$seq", sequence).With("$ts", FormatTimestamp(timestamp)).With("$sid", entry.ActorSid)
                .With("$name", entry.ActorName).With("$action", entry.Action).With("$target", entry.Target)
                .With("$outcome", entry.Outcome).With("$details", entry.DetailsJson).With("$corr", entry.CorrelationId)
                .With("$prev", previous).With("$hash", hash))
            {
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var commit = c.Command("COMMIT;"))
            {
                await commit.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return new AuditEvent(sequence, timestamp, entry.ActorSid, entry.ActorName, entry.Action, entry.Target, entry.Outcome,
                entry.DetailsJson, entry.CorrelationId, previous, hash);
        }
        catch
        {
            await using var rollback = c.Command("ROLLBACK;");
            await rollback.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new StringBuilder(" WHERE 1 = 1");
        if (query.Action is not null)
        {
            where.Append(" AND Action = $action");
        }

        if (query.From is not null)
        {
            where.Append(" AND Timestamp >= $from");
        }

        if (query.To is not null)
        {
            where.Append(" AND Timestamp < $to");
        }

        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        long total;
        await using (var count = Bind(c.Command("SELECT COUNT(*) FROM AuditEvent" + where + ";"), query))
        {
            total = Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        await using var cmd = Bind(c.Command(
            "SELECT Sequence, Timestamp, ActorSid, ActorName, Action, Target, Outcome, Details, CorrelationId, PreviousHash, Hash FROM AuditEvent"
            + where + " ORDER BY Sequence DESC LIMIT $limit OFFSET $offset;"), query)
            .With("$limit", query.PageSize).With("$offset", (long)(query.Page - 1) * query.PageSize);
        return new AuditPage(await ReadAllAsync(cmd, ct).ConfigureAwait(false), total);
    }

    public async Task<IReadOnlyList<AuditEvent>> ReadAllOrderedAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "SELECT Sequence, Timestamp, ActorSid, ActorName, Action, Target, Outcome, Details, CorrelationId, PreviousHash, Hash FROM AuditEvent ORDER BY Sequence;");
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    private static string FormatTimestamp(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static SqliteCommand Bind(SqliteCommand cmd, AuditQuery q)
    {
        if (q.Action is not null)
        {
            cmd.With("$action", q.Action);
        }

        if (q.From is not null)
        {
            cmd.With("$from", FormatTimestamp(q.From.Value));
        }

        if (q.To is not null)
        {
            cmd.With("$to", FormatTimestamp(q.To.Value));
        }

        return cmd;
    }

    private static async Task<IReadOnlyList<AuditEvent>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<AuditEvent>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AuditEvent(
                r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10)));
        }

        return list;
    }
}
