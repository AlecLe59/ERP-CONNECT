using System.Globalization;
using System.Text.Json;
using ErpConnect.Core.Abstractions;
using ErpConnect.Core.Settings;

namespace ErpConnect.Storage.Repositories;

public sealed class SettingsRepository : ISettingsRepository
{
    private const string Key = "engine";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _db;
    private readonly IClock _clock;

    public SettingsRepository(SqliteDatabase configDb, IClock clock)
    {
        _db = configDb;
        _clock = clock;
    }

    public async Task<VersionedSettings> GetAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using (var cmd = c.Command("SELECT Value, Version, UpdatedAt, UpdatedBy FROM Setting WHERE Key = $k;").With("$k", Key))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var settings = JsonSerializer.Deserialize<EngineSettings>(r.GetString(0), Json) ?? new EngineSettings();
                return new VersionedSettings(settings, r.GetInt64(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture), r.GetString(3));
            }
        }

        var defaults = new EngineSettings();
        var now = _clock.UtcNow;
        await using (var insert = c.Command(
            "INSERT OR IGNORE INTO Setting (Key, Value, Version, UpdatedAt, UpdatedBy) VALUES ($k, $v, 1, $at, 'installation');")
            .With("$k", Key).With("$v", JsonSerializer.Serialize(defaults, Json)).With("$at", now.ToString("O", CultureInfo.InvariantCulture)))
        {
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return new VersionedSettings(defaults, 1, now, "installation");
    }

    public async Task<VersionedSettings?> TryUpdateAsync(EngineSettings settings, long expectedVersion, string updatedBy, CancellationToken ct)
    {
        await GetAsync(ct).ConfigureAwait(false); // garantit l'existence de la ligne
        var now = _clock.UtcNow;
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command(
            "UPDATE Setting SET Value = $v, Version = Version + 1, UpdatedAt = $at, UpdatedBy = $by WHERE Key = $k AND Version = $expected;")
            .With("$k", Key).With("$v", JsonSerializer.Serialize(settings, Json)).With("$at", now.ToString("O", CultureInfo.InvariantCulture))
            .With("$by", updatedBy).With("$expected", expectedVersion);
        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows == 1 ? new VersionedSettings(settings, expectedVersion + 1, now, updatedBy) : null;
    }
}
