using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage;

public sealed record Migration(int Number, string Name, string Sql, string Sha256);

public sealed record MigrationReport(string Database, int FromVersion, int ToVersion, string? BackupPath);

/// <summary>Une migration déjà appliquée a été modifiée, ou la base vient d'une version plus récente : le service doit refuser de démarrer.</summary>
public sealed class MigrationIntegrityException : Exception
{
    public MigrationIntegrityException(string message)
        : base(message)
    {
    }

    public MigrationIntegrityException()
    {
    }

    public MigrationIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Applique les migrations SQL numérotées, chacune dans une transaction, après une sauvegarde cohérente.
/// Pas de retour arrière automatique : en cas d'échec la transaction est annulée et l'exception remonte.
/// </summary>
public sealed class MigrationRunner
{
    public const int BackupsToKeep = 5;

    private readonly string _backupDirectory;
    private readonly Func<DatabaseKind, IReadOnlyList<Migration>> _source;
    private readonly TimeProvider _time;

    public MigrationRunner(string backupDirectory, Func<DatabaseKind, IReadOnlyList<Migration>>? source = null, TimeProvider? time = null)
    {
        _backupDirectory = backupDirectory;
        _source = source ?? EmbeddedMigrations;
        _time = time ?? TimeProvider.System;
    }

    public static IReadOnlyList<Migration> EmbeddedMigrations(DatabaseKind kind)
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var prefix = $"ErpConnect.Storage.Migrations.{kind.ToString().ToLowerInvariant()}";
        var result = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            // Le séparateur de RecursiveDir dépend de la plateforme de build.
            var normalized = resource.Replace('\\', '.').Replace('/', '.');
            if (!normalized.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                continue;
            }

            var file = normalized[(prefix.Length + 1)..^".sql".Length];
            var number = int.Parse(file.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture);
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            result.Add(new Migration(number, file, sql, Hash(sql)));
        }

        result.Sort((a, b) => a.Number.CompareTo(b.Number));
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].Number != i + 1)
            {
                throw new InvalidOperationException($"Migrations {kind} non contiguës.");
            }
        }

        return result;
    }

    public static string Hash(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal))));

    public async Task<MigrationReport> MigrateAsync(SqliteDatabase database, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);
        var known = _source(database.Kind);
        var existed = File.Exists(database.Path) && new FileInfo(database.Path).Length > 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database.Path))!);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using (var create = connection.Command(
            "CREATE TABLE IF NOT EXISTS __schema_migrations (Number INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Sha256 TEXT NOT NULL, AppliedAt TEXT NOT NULL);"))
        {
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var applied = new List<(int Number, string Sha)>();
        await using (var read = connection.Command("SELECT Number, Sha256 FROM __schema_migrations ORDER BY Number;"))
        await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                applied.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }

        foreach (var (number, sha) in applied)
        {
            var m = known.FirstOrDefault(k => k.Number == number)
                ?? throw new MigrationIntegrityException(
                    $"La base {database.Name} a été créée par une version plus récente (migration {number} inconnue). Ouverture refusée.");
            if (!string.Equals(m.Sha256, sha, StringComparison.Ordinal))
            {
                throw new MigrationIntegrityException(
                    $"La migration {database.Name}/{m.Name} a été modifiée après son application. Démarrage refusé.");
            }
        }

        var current = applied.Count == 0 ? 0 : applied.Max(a => a.Number);
        var pending = known.Where(k => k.Number > current).ToList();
        if (pending.Count == 0)
        {
            return new MigrationReport(database.Name, current, current, null);
        }

        string? backup = null;
        if (existed && current > 0)
        {
            backup = await BackupAsync(connection, database, current, ct).ConfigureAwait(false);
        }

        foreach (var m in pending)
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var exec = connection.Command(m.Sql, tx))
            {
                await exec.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var record = connection.Command(
                "INSERT INTO __schema_migrations (Number, Name, Sha256, AppliedAt) VALUES ($n, $name, $sha, $at);", tx)
                .With("$n", m.Number).With("$name", m.Name).With("$sha", m.Sha256)
                .With("$at", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)))
            {
                await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return new MigrationReport(database.Name, current, pending[^1].Number, backup);
    }

    public static async Task<int> GetSchemaVersionAsync(SqliteDatabase database, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.Command("SELECT COALESCE(MAX(Number), 0) FROM __schema_migrations;");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task<string> BackupAsync(SqliteConnection source, SqliteDatabase database, int version, CancellationToken ct)
    {
        Directory.CreateDirectory(_backupDirectory);
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(_backupDirectory, $"{database.Name}-{stamp}-v{version}.db");
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var destination = new SqliteConnection(builder.ToString()))
        {
            await destination.OpenAsync(ct).ConfigureAwait(false);
            source.BackupDatabase(destination);
        }

        var old = Directory.GetFiles(_backupDirectory, $"{database.Name}-*.db")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Skip(BackupsToKeep);
        foreach (var f in old)
        {
            File.Delete(f);
        }

        return path;
    }
}
