using System.Globalization;
using ErpConnect.Core.Flows;
using ErpConnect.Core.Runs;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage.Repositories;

public sealed class RunRepository : IRunRepository
{
    private readonly SqliteDatabase _db;

    public RunRepository(SqliteDatabase runsDb) => _db = runsDb;

    private const string Columns =
        "Id, FlowId, FlowVersion, Status, TriggeredBy, ParentRunId, CreatedAt, StartedAt, EndedAt, ReadCount, WrittenCount, RejectedCount, SkippedCount, Checkpoint, Error, TriggerKind";

    private static string Iso(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Date(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);

    private static RunRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt64(2), (RunStatus)r.GetInt32(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), Date(r, 7), Date(r, 8),
        r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14),
        r.IsDBNull(15) ? null : r.GetString(15));

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

        await using (var u = c.Command(
            "UPDATE Run SET ReadCount = $r, WrittenCount = $w, RejectedCount = $j, SkippedCount = $s, Checkpoint = $cp WHERE Id = $id;", tx)
            .With("$id", id).With("$r", progress.Read).With("$w", progress.Written).With("$j", progress.Rejected).With("$s", progress.Skipped).With("$cp", progress.Checkpoint))
        {
            await u.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SetErrorAsync(string id, string error, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var u = c.Command("UPDATE Run SET Error = $e WHERE Id = $id;").With("$id", id).With("$e", error);
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
}
