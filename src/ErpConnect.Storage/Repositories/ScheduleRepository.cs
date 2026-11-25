using System.Globalization;
using ErpConnect.Core.Flows;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage.Repositories;

public sealed class ScheduleRepository : IScheduleRepository
{
    private const string Columns = "FlowId, Cron, TimeZone, Enabled, MaxRetries, RetryDelaySeconds, Version, UpdatedAt, UpdatedBy, NextRunAt, LastRunId, RetryAttempt, RetryAt";
    private readonly SqliteDatabase _db;

    public ScheduleRepository(SqliteDatabase configDb) => _db = configDb;

    private static string Iso(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);

    private static object? Iso(DateTimeOffset? d) => d is null ? null : Iso(d.Value);

    private static DateTimeOffset? Date(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);

    private static ScheduleRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1, r.GetInt32(4), r.GetInt32(5), r.GetInt64(6),
        DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture), r.GetString(8), Date(r, 9), r.IsDBNull(10) ? null : r.GetString(10), r.GetInt32(11), Date(r, 12));

    public async Task<IReadOnlyList<ScheduleRecord>> ListAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var q = c.Command($"SELECT {Columns} FROM Schedule ORDER BY FlowId;");
        await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ScheduleRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Read(r));
        }

        return list;
    }

    public async Task<ScheduleRecord?> GetAsync(string flowId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var q = c.Command($"SELECT {Columns} FROM Schedule WHERE FlowId = $f;").With("$f", flowId);
        await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task<ScheduleRecord?> SaveAsync(ScheduleRecord record, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        int changed;
        try
        {
            await using var cmd = expectedVersion == 0
                ? c.Command("""
                    INSERT INTO Schedule (FlowId, Cron, TimeZone, Enabled, MaxRetries, RetryDelaySeconds, Version, UpdatedAt, UpdatedBy, NextRunAt)
                    VALUES ($f, $c, $tz, $e, $m, $d, 1, $at, $by, $next);
                    """)
                : c.Command("""
                    UPDATE Schedule SET Cron = $c, TimeZone = $tz, Enabled = $e, MaxRetries = $m, RetryDelaySeconds = $d, Version = Version + 1,
                        UpdatedAt = $at, UpdatedBy = $by, NextRunAt = $next, RetryAttempt = 0, RetryAt = NULL
                    WHERE FlowId = $f AND Version = $v;
                    """).With("$v", expectedVersion);
            cmd.With("$f", record.FlowId).With("$c", record.Cron).With("$tz", record.TimeZone).With("$e", record.Enabled ? 1 : 0)
                .With("$m", record.MaxRetries).With("$d", record.RetryDelaySeconds).With("$at", Iso(record.UpdatedAt)).With("$by", record.UpdatedBy).With("$next", Iso(record.NextRunAt));
            changed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return null;
        }

        return changed == 1 ? await GetAsync(record.FlowId, ct).ConfigureAwait(false) : null;
    }

    public async Task<bool> DeleteAsync(string flowId, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("DELETE FROM Schedule WHERE FlowId = $f;").With("$f", flowId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task SetStateAsync(string flowId, DateTimeOffset? nextRunAt, string? lastRunId, int retryAttempt, DateTimeOffset? retryAt, CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("UPDATE Schedule SET NextRunAt = $n, LastRunId = $l, RetryAttempt = $a, RetryAt = $r WHERE FlowId = $f;")
            .With("$f", flowId).With("$n", Iso(nextRunAt)).With("$l", lastRunId).With("$a", retryAttempt).With("$r", Iso(retryAt));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
