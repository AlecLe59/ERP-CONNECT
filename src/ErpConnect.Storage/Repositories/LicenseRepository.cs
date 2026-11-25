using System.Globalization;
using ErpConnect.Core.Licensing;

namespace ErpConnect.Storage.Repositories;

public sealed class LicenseRepository : ILicenseStore
{
    private readonly SqliteDatabase _db;

    public LicenseRepository(SqliteDatabase configDb) => _db = configDb;

    public async Task<StoredLicense?> GetAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var q = c.Command("SELECT Content, LicenseId, ImportedAt, ImportedBy FROM License WHERE Id = 1;");
        await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false)
            ? new StoredLicense(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture), r.GetString(3))
            : null;
    }

    public async Task SaveAsync(StoredLicense license, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(license);
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("""
            INSERT INTO License (Id, Content, LicenseId, ImportedAt, ImportedBy) VALUES (1, $c, $l, $at, $by)
            ON CONFLICT (Id) DO UPDATE SET Content = excluded.Content, LicenseId = excluded.LicenseId, ImportedAt = excluded.ImportedAt, ImportedBy = excluded.ImportedBy;
            """).With("$c", license.Content).With("$l", license.LicenseId)
            .With("$at", license.ImportedAt.ToString("O", CultureInfo.InvariantCulture)).With("$by", license.ImportedBy);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(CancellationToken ct)
    {
        await using var c = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.Command("DELETE FROM License WHERE Id = 1;");
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }
}
