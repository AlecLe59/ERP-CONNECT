using ErpConnect.Core.Authorization;
using ErpConnect.Core.Settings;
using ErpConnect.Storage;
using ErpConnect.Storage.Repositories;
using ErpConnect.Tests.Shared;

namespace ErpConnect.Storage.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<SqliteDatabase> ConfigAsync()
    {
        var db = SqliteDatabase.InDirectory(DatabaseKind.Config, _dir.Path);
        await new MigrationRunner(Path.Combine(_dir.Path, "backup")).MigrateAsync(db, default);
        return db;
    }

    [Fact]
    public async Task Settings_use_optimistic_concurrency_and_persist()
    {
        var db = await ConfigAsync();
        var repo = new SettingsRepository(db, new FakeClock());
        var initial = await repo.GetAsync(default);
        initial.Version.ShouldBe(1);

        var updated = await repo.TryUpdateAsync(initial.Settings with { FailedLogRetentionDays = 120 }, 1, "PC\\admin", default);
        updated.ShouldNotBeNull().Version.ShouldBe(2);
        (await repo.TryUpdateAsync(initial.Settings, 1, "PC\\other", default)).ShouldBeNull(); // version dépassée

        var reread = await new SettingsRepository(db, new FakeClock()).GetAsync(default);
        reread.Settings.FailedLogRetentionDays.ShouldBe(120);
        reread.UpdatedBy.ShouldBe("PC\\admin");
    }

    [Fact]
    public async Task Role_assignments_are_unique_and_removable()
    {
        var repo = new RoleAssignmentRepository(await ConfigAsync());
        var a = new RoleAssignment(0, "S-1-5-21-1-2-3-1001", "PC\\op", PrincipalKind.User, Role.Operator, ScopeKind.Global, null, DateTimeOffset.UnixEpoch, "t");
        var first = await repo.AddAsync(a, default);
        var again = await repo.AddAsync(a, default);
        again.Id.ShouldBe(first.Id);
        (await repo.ListAsync(default)).Count.ShouldBe(1);
        (await repo.RemoveAsync(first.Id, default)).ShouldBeTrue();
        (await repo.RemoveAsync(first.Id, default)).ShouldBeFalse();
    }

    [Fact]
    public async Task Installation_is_created_once()
    {
        var repo = new InstallationRepository(await ConfigAsync());
        var a = await repo.GetOrCreateAsync(default);
        var b = await repo.GetOrCreateAsync(default);
        b.Id.ShouldBe(a.Id);
        a.KeyEntropy.Length.ShouldBe(32);
    }

    [Fact]
    public async Task Secret_rows_enforce_nonce_and_tag_sizes()
    {
        var db = await ConfigAsync();
        var store = new SecretStore(db);
        await store.RegisterKeyVersionAsync(1, default);
        var bad = new ErpConnect.Core.Abstractions.SecretRecord("x", "u", 1, new byte[5], new byte[3], new byte[16], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.UpsertAsync(bad, default));
    }
}
