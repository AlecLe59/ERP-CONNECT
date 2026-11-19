using System.Globalization;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Runs;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage.Repositories;

public sealed class RunRepository : IRunRepository, IDedupRegistry, IAlertRepository, IRetentionStore
{
    private readonly SqliteDatabase _db;

    public RunRepository(SqliteDatabase runsDb) => _db = runsDb;

    private const string Columns =
        "Id, FlowId, FlowVersion, Status, TriggeredBy, ParentRunId, CreatedAt, StartedAt, EndedAt, ReadCount, WrittenCount, RejectedCount, SkippedCount, Checkpoint, Error, TriggerKind, ErrorKind";

    private static string Iso(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Date(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);

    private static RunRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt64(2), (RunStatus)r.GetInt32(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), Date(r, 7), Date(r, 8),
        r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14),
        r.IsDBNull(15) ? null : r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16));

    public async Task CreateAsync(RunRecord run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = c.Command(
            """
            INSERT INTO Run (Id, FlowId, FlowVersion, Status, TriggeredBy, ParentRunId, CreatedAt, Checkpoint, TriggerKind)
            VALUES ($id, $f, $v, $s, $by, $p, $at, $cp, $tk);
            """, tx).With("$id", run.Id).With("$f", run.FlowId).With("$v", run.FlowVersion).With("$s", (int)run.Status).With("$by", run.TriggeredBy)
            .With("$p", run.ParentRunId).With("$at", Iso(run.CreatedAt)).With("$cp", run.Checkpoint).With("$tk", run.Trigger))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var h = c.Command("INSERT INTO RunStatusHistory (RunId, FromStatus, ToStatus, At, Actor, Reason) VALUES ($id, NULL, $s, $at, $by, NULL);", tx)
            .With("$id", run.Id).With("$s", (int)run.Status).With("$at", Iso(run.CreatedAt)).With("$by", run.TriggeredBy))
        {
            await h.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<RunRecord?> GetAsync(string id, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command($"SELECT {Columns} FROM Run WHERE Id = $id;").With("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task<(IReadOnlyList<RunRecord> Items, int Total)> QueryAsync(RunQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        const string Where = "WHERE ($f IS NULL OR FlowId = $f) AND ($s IS NULL OR Status = $s)";
        int total;
        await using (var count = c.Command($"SELECT COUNT(*) FROM Run {Where};").With("$f", query.FlowId).With("$s", query.Status is null ? null : (int)query.Status))
        {
            total = Convert.ToInt32(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        await using var cmd = c.Command($"SELECT {Columns} FROM Run {Where} ORDER BY CreatedAt DESC, Id DESC LIMIT $l OFFSET $o;")
            .With("$f", query.FlowId).With("$s", query.Status is null ? null : (int)query.Status).With("$l", query.PageSize).With("$o", (query.Page - 1) * query.PageSize);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RunRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Read(r));
        }

        return (list, total);
    }

    public async Task<bool> TransitionAsync(string id, RunStatus from, RunStatus to, string actor, string? reason, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var started = to == RunStatus.Running ? ", StartedAt = COALESCE(StartedAt, $at)" : string.Empty;
        var ended = to.IsTerminal() ? ", EndedAt = $at" : string.Empty;
        await using (var u = c.Command($"UPDATE Run SET Status = $to{started}{ended} WHERE Id = $id AND Status = $from;", tx)
            .With("$id", id).With("$from", (int)from).With("$to", (int)to).With("$at", Iso(at)))
        {
            if (await u.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return false;
            }
        }

        await using (var h = c.Command("INSERT INTO RunStatusHistory (RunId, FromStatus, ToStatus, At, Actor, Reason) VALUES ($id, $from, $to, $at, $by, $r);", tx)
            .With("$id", id).With("$from", (int)from).With("$to", (int)to).With("$at", Iso(at)).With("$by", actor).With("$r", reason))
        {
            await h.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task SaveProgressAsync(string id, RunProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var rej in progress.NewRejects)
        {
            await using var ins = c.Command(
                "INSERT INTO RunReject (RunId, BatchNumber, RecordIndex, Stage, Reason, RecordJson, At) VALUES ($id, $b, $i, $st, $r, $j, $at);", tx)
                .With("$id", id).With("$b", rej.BatchNumber).With("$i", rej.RecordIndex).With("$st", rej.Stage).With("$r", rej.Reason).With("$j", rej.RecordJson).With("$at", Iso(rej.At));
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (progress.NewDedupKeys is { Count: > 0 } keys)
        {
            var flowId = await FlowOfAsync(c, tx, id, ct).ConfigureAwait(false);
            await InsertKeysAsync(c, tx, flowId, id, keys, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }

        await using (var u = c.Command(
            "UPDATE Run SET ReadCount = $r, WrittenCount = $w, RejectedCount = $j, SkippedCount = $s, Checkpoint = $cp WHERE Id = $id;", tx)
            .With("$id", id).With("$r", progress.Read).With("$w", progress.Written).With("$j", progress.Rejected).With("$s", progress.Skipped).With("$cp", progress.Checkpoint))
        {
            await u.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SetErrorAsync(string id, string error, CancellationToken ct, string? kind = null)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var u = c.Command("UPDATE Run SET Error = $e, ErrorKind = $k WHERE Id = $id;").With("$id", id).With("$e", error).With("$k", kind);
        await u.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunRejectRecord>> RejectsAsync(string runId, int limit, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("SELECT Id, RunId, BatchNumber, RecordIndex, Stage, Reason, RecordJson, At FROM RunReject WHERE RunId = $id ORDER BY Id LIMIT $l;")
            .With("$id", runId).With("$l", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RunRejectRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new RunRejectRecord(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture)));
        }

        return list;
    }

    public async Task LogAsync(string runId, DateTimeOffset at, string level, string message, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("INSERT INTO RunLog (RunId, At, Level, Message) VALUES ($id, $at, $l, $m);")
            .With("$id", runId).With("$at", Iso(at)).With("$l", level).With("$m", message.Length > 4000 ? message[..4000] : message);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RunLogRecord>> LogsAsync(string runId, int limit, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("SELECT Id, RunId, At, Level, Message FROM RunLog WHERE RunId = $id ORDER BY Id LIMIT $l;").With("$id", runId).With("$l", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RunLogRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new RunLogRecord(r.GetInt64(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture), r.GetString(3), r.GetString(4)));
        }

        return list;
    }

    public async Task<IReadOnlyList<RunRecord>> InStatusAsync(IReadOnlyList<RunStatus> statuses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var all = new List<RunRecord>();
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        foreach (var s in statuses)
        {
            await using var cmd = c.Command($"SELECT {Columns} FROM Run WHERE Status = $s ORDER BY CreatedAt;").With("$s", (int)s);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                all.Add(Read(r));
            }
        }

        return all;
    }

    private static async Task<string> FlowOfAsync(SqliteConnection c, SqliteTransaction tx, string runId, CancellationToken ct)
    {
        await using var q = c.Command("SELECT FlowId FROM Run WHERE Id = $id;", tx).With("$id", runId);
        return (string?)await q.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Exécution inconnue.");
    }

    private static async Task InsertKeysAsync(SqliteConnection c, SqliteTransaction tx, string flowId, string runId, IReadOnlyCollection<string> keys, DateTimeOffset at, CancellationToken ct)
    {
        await using var ins = c.Command("INSERT OR IGNORE INTO DedupKey (FlowId, KeyHash, RunId, At) VALUES ($f, $k, $r, $at);", tx)
            .With("$f", flowId).With("$k", string.Empty).With("$r", runId).With("$at", Iso(at));
        foreach (var k in keys)
        {
            ins.Parameters["$k"].Value = k;
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlySet<string>> ExistingAsync(string flowId, IReadOnlyCollection<string> keyHashes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyHashes);
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (keyHashes.Count == 0)
        {
            return found;
        }

        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        foreach (var chunk in keyHashes.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            var names = chunk.Select((_, i) => "$k" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
            await using var q = c.Command($"SELECT KeyHash FROM DedupKey WHERE FlowId = $f AND KeyHash IN ({string.Join(',', names)});").With("$f", flowId);
            for (var i = 0; i < chunk.Length; i++)
            {
                q.Parameters.AddWithValue(names[i], chunk[i]);
            }

            await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                found.Add(r.GetString(0));
            }
        }

        return found;
    }

    public async Task RegisterAsync(string flowId, string runId, IReadOnlyCollection<string> keyHashes, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyHashes);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await InsertKeysAsync(c, tx, flowId, runId, keyHashes, at, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static AlertRecord ReadAlert(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4),
        DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture), Date(r, 6), r.IsDBNull(7) ? null : r.GetString(7));

    public async Task<AlertRecord> RaiseAsync(string flowId, string? runId, string severity, string message, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var ins = c.Command("INSERT INTO Alert (FlowId, RunId, Severity, Message, CreatedAt) VALUES ($f, $r, $s, $m, $at) RETURNING Id;")
            .With("$f", flowId).With("$r", runId).With("$s", severity).With("$m", message).With("$at", Iso(at));
        var id = (long)(await ins.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        return new AlertRecord(id, flowId, runId, severity, message, at, null, null);
    }

    public async Task<IReadOnlyList<AlertRecord>> ListAsync(bool openOnly, int limit, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var q = c.Command($"SELECT Id, FlowId, RunId, Severity, Message, CreatedAt, AcknowledgedAt, AcknowledgedBy FROM Alert {(openOnly ? "WHERE AcknowledgedAt IS NULL" : string.Empty)} ORDER BY Id DESC LIMIT $l;")
            .With("$l", limit);
        await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<AlertRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAlert(r));
        }

        return list;
    }

    public async Task<bool> AcknowledgeAsync(long id, string by, DateTimeOffset at, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var u = c.Command("UPDATE Alert SET AcknowledgedAt = $at, AcknowledgedBy = $by WHERE Id = $id AND AcknowledgedAt IS NULL;")
            .With("$id", id).With("$at", Iso(at)).With("$by", by);
        return await u.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Purge selon les durées de rétention, dans une transaction. Seules les exécutions terminées sont concernées ;
    /// une exécution encore référencée comme parente par une exécution conservée est gardée (lignée de reprise).
    /// </summary>
    public async Task<PurgeCounts> PurgeAsync(RetentionCutoffs cutoffs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cutoffs);
        const string Terminal = "Status IN (2, 3, 4, 6) AND EndedAt IS NOT NULL";
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        async Task<long> Exec(string sql, params (string Name, object Value)[] p)
        {
            await using var cmd = c.Command(sql, tx);
            foreach (var (n, v) in p)
            {
                cmd.With(n, v);
            }

            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var logs = await Exec($"DELETE FROM RunLog WHERE RunId IN (SELECT Id FROM Run WHERE {Terminal} AND Status IN (2, 3) AND EndedAt < $c);", ("$c", Iso(cutoffs.SucceededLogs)));
        logs += await Exec($"DELETE FROM RunLog WHERE RunId IN (SELECT Id FROM Run WHERE {Terminal} AND Status IN (4, 6) AND EndedAt < $c);", ("$c", Iso(cutoffs.FailedLogs)));
        var rejects = await Exec($"DELETE FROM RunReject WHERE RunId IN (SELECT Id FROM Run WHERE {Terminal} AND EndedAt < $c);", ("$c", Iso(cutoffs.Rejects)));
        var dedup = await Exec("DELETE FROM DedupKey WHERE At < $c;", ("$c", Iso(cutoffs.DedupKeys)));
        var alerts = await Exec("DELETE FROM Alert WHERE AcknowledgedAt IS NOT NULL AND CreatedAt < $c;", ("$c", Iso(cutoffs.RunHeaders)));
        long runs = 0;
        for (var pass = 0; pass < 20; pass++) // les enfants d'abord, puis leurs parents au passage suivant
        {
            const string Old = "SELECT Id FROM Run r WHERE " + Terminal + " AND EndedAt < $c AND NOT EXISTS (SELECT 1 FROM Run k WHERE k.ParentRunId = r.Id)";
            await Exec($"DELETE FROM RunLog WHERE RunId IN ({Old});", ("$c", Iso(cutoffs.RunHeaders)));
            await Exec($"DELETE FROM RunReject WHERE RunId IN ({Old});", ("$c", Iso(cutoffs.RunHeaders)));
            await Exec($"DELETE FROM RunStatusHistory WHERE RunId IN ({Old});", ("$c", Iso(cutoffs.RunHeaders)));
            var deleted = await Exec($"DELETE FROM Run WHERE Id IN ({Old});", ("$c", Iso(cutoffs.RunHeaders)));
            runs += deleted;
            if (deleted == 0)
            {
                break;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new PurgeCounts(runs, logs, rejects, dedup, alerts);
    }
}
