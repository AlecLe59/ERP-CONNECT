using ErpConnect.Storage;
using ErpConnect.Tests.Shared;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage.Tests;

public sealed class MigrationRunnerTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Backup => Path.Combine(_dir.Path, "backup");

    private SqliteDatabase Db(DatabaseKind kind = DatabaseKind.Config) => SqliteDatabase.InDirectory(kind, _dir.Path);

    [Theory]
    [InlineData(DatabaseKind.Config)]
    [InlineData(DatabaseKind.Runs)]
    [InlineData(DatabaseKind.Audit)]
    public async Task Empty_database_is_migrated_to_latest(DatabaseKind kind)
    {
        var report = await new MigrationRunner(Backup).MigrateAsync(Db(kind), default);
        report.FromVersion.ShouldBe(0);
        report.ToVersion.ShouldBe(MigrationRunner.EmbeddedMigrations(kind).Count);
        report.BackupPath.ShouldBeNull(); // rien à sauvegarder sur une base neuve
        (await MigrationRunner.GetSchemaVersionAsync(Db(kind), default)).ShouldBe(report.ToVersion);
    }

    [Fact]
    public async Task Phase2_tables_exist()
    {
        var runner = new MigrationRunner(Backup);
        foreach (var k in Enum.GetValues<DatabaseKind>())
        {
            await runner.MigrateAsync(Db(k), default);
        }

        new[] { "Installation", "Setting", "Principal", "RoleAssignment", "Folder", "Secret", "KeyVersion" }.ShouldBeSubsetOf(await Tables(DatabaseKind.Config));
        (await Tables(DatabaseKind.Audit)).ShouldContain("AuditEvent");
        new[] { "Run", "RunStatusHistory" }.ShouldBeSubsetOf(await Tables(DatabaseKind.Runs));
    }

    [Fact]
    public async Task Connection_pragmas_are_applied()
    {
        await new MigrationRunner(Backup).MigrateAsync(Db(), default);
        await using var c = await Db().OpenAsync(default);
        (await Scalar(c, "PRAGMA journal_mode;")).ShouldBe("wal");
        (await Scalar(c, "PRAGMA foreign_keys;")).ShouldBe("1");
        (await Scalar(c, "PRAGMA busy_timeout;")).ShouldBe("5000");
    }

    [Fact]
    public async Task Running_twice_is_a_no_op()
    {
        var runner = new MigrationRunner(Backup);
        await runner.MigrateAsync(Db(), default);
        var second = await runner.MigrateAsync(Db(), default);
        second.FromVersion.ShouldBe(second.ToVersion);
        second.BackupPath.ShouldBeNull();
    }

    // S16 : une migration modifiée après publication empêche le démarrage.
    [Fact]
    public async Task Modified_applied_migration_is_refused()
    {
        await new MigrationRunner(Backup).MigrateAsync(Db(), default);
        var tampered = MigrationRunner.EmbeddedMigrations(DatabaseKind.Config)
            .Select(m => m.Number == 1 ? m with { Sha256 = MigrationRunner.Hash(m.Sql + "\n-- modifié") } : m).ToList();
        var ex = await Should.ThrowAsync<MigrationIntegrityException>(() => new MigrationRunner(Backup, _ => tampered).MigrateAsync(Db(), default));
        ex.Message.ShouldContain("modifiée");
    }

    [Fact]
    public async Task Database_from_a_newer_version_is_refused()
    {
        var future = MigrationRunner.EmbeddedMigrations(DatabaseKind.Config).Append(new Migration(99, "0099_future", "SELECT 1;", MigrationRunner.Hash("SELECT 1;"))).ToList();
        await new MigrationRunner(Backup, _ => future).MigrateAsync(Db(), default);
        var ex = await Should.ThrowAsync<MigrationIntegrityException>(() => new MigrationRunner(Backup).MigrateAsync(Db(), default));
        ex.Message.ShouldContain("plus récente");
    }

    [Fact]
    public async Task Existing_database_is_backed_up_before_migration_and_only_five_backups_are_kept()
    {
        var v1 = MigrationRunner.EmbeddedMigrations(DatabaseKind.Config).ToList();
        var time = new ManualTime(new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero));
        await new MigrationRunner(Backup, _ => v1, time).MigrateAsync(Db(), default);
        await using (var c = await Db().OpenAsync(default))
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO Folder (Id, Name, CreatedAt) VALUES ('f1', 'Ventes', '2026-01-01');";
            await cmd.ExecuteNonQueryAsync();
        }

        var migrations = v1;
        for (var i = 0; i < 7; i++)
        {
            var sql = $"CREATE TABLE T{i} (X INTEGER);";
            migrations = migrations.Append(new Migration(migrations.Count + 1, $"{migrations.Count + 1:0000}_t{i}", sql, MigrationRunner.Hash(sql))).ToList();
            time.Advance(TimeSpan.FromMinutes(1));
            var current = migrations;
            var report = await new MigrationRunner(Backup, _ => current, time).MigrateAsync(Db(), default);
            report.BackupPath.ShouldNotBeNull();
            File.Exists(report.BackupPath).ShouldBeTrue();
        }

        var backups = Directory.GetFiles(Backup, "config-*.db");
        backups.Length.ShouldBe(MigrationRunner.BackupsToKeep);

        // La sauvegarde est une copie cohérente et lisible contenant les données.
        await using var b = new SqliteConnection($"Data Source={backups.Max()};Mode=ReadOnly;Pooling=False");
        await b.OpenAsync();
        (await Scalar(b, "SELECT Name FROM Folder WHERE Id = 'f1';")).ShouldBe("Ventes");
    }

    [Fact]
    public async Task Failed_migration_is_rolled_back()
    {
        var embedded = MigrationRunner.EmbeddedMigrations(DatabaseKind.Config);
        var broken = embedded
            .Append(new Migration(embedded.Count + 1, $"{embedded.Count + 1:0000}_broken", "CREATE TABLE Ok (X INT); THIS IS NOT SQL;", "x")).ToList();
        await Should.ThrowAsync<SqliteException>(() => new MigrationRunner(Backup, _ => broken).MigrateAsync(Db(), default));
        (await Tables(DatabaseKind.Config)).ShouldNotContain("Ok");
        (await MigrationRunner.GetSchemaVersionAsync(Db(), default)).ShouldBe(embedded.Count);
    }

    [Fact]
    public async Task Audit_table_is_append_only()
    {
        await new MigrationRunner(Backup).MigrateAsync(Db(DatabaseKind.Audit), default);
        await using var c = await Db(DatabaseKind.Audit).OpenAsync(default);
        await using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO AuditEvent VALUES (1, 't', 's', 'n', 'a', 't', 'o', NULL, 'c', 'p', 'h');";
            await ins.ExecuteNonQueryAsync();
        }

        foreach (var sql in new[] { "UPDATE AuditEvent SET Outcome = 'x';", "DELETE FROM AuditEvent;" })
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            (await Should.ThrowAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync())).Message.ShouldContain("ajout seul");
        }
    }

    private async Task<List<string>> Tables(DatabaseKind kind)
    {
        await using var c = await Db(kind).OpenAsync(default);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<string>();
        while (await r.ReadAsync())
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    private static async Task<string?> Scalar(SqliteConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
