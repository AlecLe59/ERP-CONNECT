using Microsoft.Data.Sqlite;

namespace ErpConnect.Storage;

/// <summary>Les trois bases locales. Seul le service y écrit.</summary>
public enum DatabaseKind
{
    Config,
    Runs,
    Audit,
}

/// <summary>Ouvre les connexions SQLite avec les réglages imposés (WAL, clés étrangères, attente de verrou).</summary>
public sealed class SqliteDatabase
{
    public SqliteDatabase(DatabaseKind kind, string path)
    {
        Kind = kind;
        Path = path;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public DatabaseKind Kind { get; }

    public string Path { get; }

    public string ConnectionString { get; }

    public string Name => Kind.ToString().ToLowerInvariant();

    public static SqliteDatabase InDirectory(DatabaseKind kind, string dataDirectory) =>
        new(kind, System.IO.Path.Combine(dataDirectory, $"{kind.ToString().ToLowerInvariant()}.db"));

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var synchronous = Kind == DatabaseKind.Audit ? "FULL" : "NORMAL";
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous={synchronous};";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal static class SqliteExtensions
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        var cmd = connection.CreateCommand();
#pragma warning disable CA2100 // Toutes les requêtes du dépôt sont des constantes ; les valeurs passent par des paramètres.
        cmd.CommandText = sql;
#pragma warning restore CA2100
        cmd.Transaction = tx;
        return cmd;
    }

    public static SqliteCommand With(this SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
