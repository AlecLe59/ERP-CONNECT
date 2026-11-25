using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ErpConnect.Connectors.Sdk;
using Microsoft.Data.Sqlite;

namespace ErpConnect.Connectors;

/// <summary>
/// Base SQLite (fichier). Lecture d'une table (pagination par rowid, reprise exacte) ou d'une requête SELECT
/// (pagination par décalage : la requête doit avoir un ORDER BY stable). La lecture ouvre la base en lecture seule :
/// une requête ne peut rien modifier. Écriture dans une table : insertion ou remplacement (upsert), un point de
/// sauvegarde par enregistrement pour qu'une contrainte violée ne rejette que la ligne fautive.
/// </summary>
public sealed class SqliteConnector : IConnector
{
    public ConnectorManifest Manifest { get; } = new(
        "db.sqlite", "Base SQLite", "1.0.0",
        ConnectorCapabilities.Read | ConnectorCapabilities.Write | ConnectorCapabilities.Discover,
        [
            new("path", "Fichier de base", ConfigFieldType.Path, Required: true),
            new("timeoutSeconds", "Délai d'attente si la base est occupée (s)", ConfigFieldType.Number, Default: "30"),
        ],
        "Lecture de tables ou requêtes SELECT, écriture par insertion ou remplacement.");

    internal static string Quote(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 128 || identifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Nom de table ou de colonne invalide.");
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    internal static bool IsQuery(string resource)
    {
        var t = resource.TrimStart();
        return t.StartsWith("select ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("with ", StringComparison.OrdinalIgnoreCase);
    }

    internal static string CheckQuery(string sql)
    {
        var t = sql.Trim().TrimEnd(';').Trim();
        if (t.Contains(';', StringComparison.Ordinal))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Une seule requête SELECT est autorisée.");
        }

        return t;
    }

    internal static SqliteConnection Open(ConnectorSettings settings, bool readOnly)
    {
        var path = settings.Require("path");
        if (readOnly && !File.Exists(path))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Fichier de base introuvable.");
        }

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            DefaultTimeout = settings.GetInt("timeoutSeconds", 30),
            Pooling = false,
        };
        var c = new SqliteConnection(cs.ToString());
        try
        {
            c.Open();
        }
        catch (SqliteException ex)
        {
            c.Dispose();
            throw Map(ex);
        }

        return c;
    }

    /// <summary>Base occupée/verrouillée = transitoire ; le reste (syntaxe, contrainte, droits) = permanent.</summary>
    internal static ConnectorException Map(SqliteException ex) => new(
        ex.SqliteErrorCode is 5 or 6 ? ErrorKind.Transient : ErrorKind.Permanent,
        ex.SqliteErrorCode is 5 or 6 ? "Base occupée." : $"SQLite : {ex.Message}", ex);

    public Task<ConnectionTestResult> TestAsync(ConnectorSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sw = Stopwatch.StartNew();
        try
        {
            using var c = Open(settings, readOnly: true);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table';";
            var n = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            return Task.FromResult(new ConnectionTestResult(true, $"Base ouverte, {n} table(s).", sw.Elapsed));
        }
        catch (ConnectorException ex)
        {
            return Task.FromResult(new ConnectionTestResult(false, ex.Message, sw.Elapsed));
        }
    }

    public Task<DataStructure> DiscoverAsync(ConnectorSettings settings, string? resource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var c = Open(settings, readOnly: true);
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                tables.Add(r.GetString(0));
            }
        }

        var fields = new List<FieldInfo>();
        if (!string.IsNullOrWhiteSpace(resource) && !IsQuery(resource))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT name, type FROM pragma_table_info($t);";
            cmd.Parameters.AddWithValue("$t", resource);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                fields.Add(new FieldInfo(r.GetString(0), r.GetString(1)));
            }
        }

        return Task.FromResult(new DataStructure(tables, fields));
    }

    public IRecordReader CreateReader(ConnectorSettings settings, ReadRequest request) => new Reader(settings, request);

    public IRecordWriter CreateWriter(ConnectorSettings settings, WriteRequest request) => new Writer(settings, request);

    private sealed class Reader(ConnectorSettings settings, ReadRequest request) : IRecordReader
    {
        public async IAsyncEnumerable<RecordBatch> ReadAsync(string? cursor, [EnumeratorCancellation] CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            var query = IsQuery(request.Resource);
            var sql = query
                ? $"SELECT * FROM ({CheckQuery(request.Resource)}) LIMIT $n OFFSET $c;"
                : $"SELECT rowid AS \"__rowid\", * FROM {Quote(request.Resource.Trim())} WHERE rowid > $c ORDER BY rowid LIMIT $n;";
            long position = cursor is null ? 0 : long.Parse(cursor, NumberStyles.None, CultureInfo.InvariantCulture);
            using var c = Open(settings, readOnly: true);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var batch = new List<Record>(request.BatchSize);
                using (var cmd = c.CreateCommand())
                {
#pragma warning disable CA2100 // Requête définie par l'administrateur du flux ; base ouverte en lecture seule, valeurs paramétrées.
                    cmd.CommandText = sql;
#pragma warning restore CA2100
                    cmd.Parameters.AddWithValue("$n", request.BatchSize);
                    cmd.Parameters.AddWithValue("$c", position);
                    SqliteDataReader r;
                    try
                    {
                        r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    }
                    catch (SqliteException ex)
                    {
                        throw Map(ex);
                    }

                    using (r)
                    {
                        while (await r.ReadAsync(ct).ConfigureAwait(false))
                        {
                            var rec = new Record();
                            for (var i = 0; i < r.FieldCount; i++)
                            {
                                var name = r.GetName(i);
                                if (!query && i == 0)
                                {
                                    position = r.GetInt64(0);
                                    continue;
                                }

                                rec.Set(name, r.IsDBNull(i) ? null : r.GetValue(i) switch
                                {
                                    long l => l,
                                    double d => d,
                                    string s => s,
                                    byte[] b => b,
                                    var o => Convert.ToString(o, CultureInfo.InvariantCulture),
                                });
                            }

                            batch.Add(rec);
                        }
                    }
                }

                if (batch.Count == 0)
                {
                    yield break;
                }

                if (query)
                {
                    position += batch.Count;
                }

                yield return new RecordBatch(batch, position.ToString(CultureInfo.InvariantCulture));
                if (batch.Count < request.BatchSize)
                {
                    yield break;
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Writer : IRecordWriter
    {
        private readonly SqliteConnection _c;
        private readonly string _table;
        private readonly string _verb;

        public Writer(ConnectorSettings settings, WriteRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            _table = Quote(request.Resource.Trim());
            _verb = (request.Option("mode") ?? "insert") switch
            {
                "insert" => "INSERT",
                "upsert" => "INSERT OR REPLACE",
                _ => throw new ConnectorException(ErrorKind.Permanent, "Mode d'écriture : insert ou upsert."),
            };
            _c = Open(settings, readOnly: false);
        }

        private static object Value(object? v) => v switch
        {
            null => DBNull.Value,
            bool b => b ? 1L : 0L,
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Record or IReadOnlyList<object?> => JsonText(v),
            _ => v,
        };

        private static string JsonText(object v)
        {
            using var ms = new MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(ms))
            {
                RecordJson.WriteValue(w, v);
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public async Task<WriteResult> WriteBatchAsync(IReadOnlyList<Record> batch, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(batch);
            try
            {
                return await WriteCoreAsync(batch, ct).ConfigureAwait(false);
            }
            catch (SqliteException ex)
            {
                throw Map(ex); // base occupée à l'ouverture de la transaction ou à la validation
            }
        }

        private async Task<WriteResult> WriteCoreAsync(IReadOnlyList<Record> batch, CancellationToken ct)
        {
            var outcomes = new RecordOutcome[batch.Count];
            await using var tx = (SqliteTransaction)await _c.BeginTransactionAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var names = batch[i].Names.ToList();
                using var cmd = _c.CreateCommand();
                cmd.Transaction = tx;
#pragma warning disable CA2100 // Identifiants échappés par Quote, valeurs toujours paramétrées.
                cmd.CommandText = $"SAVEPOINT r; {_verb} INTO {_table} ({string.Join(", ", names.Select(Quote))}) VALUES ({string.Join(", ", names.Select((_, k) => "$p" + k.ToString(CultureInfo.InvariantCulture)))}); RELEASE r;";
#pragma warning restore CA2100
                for (var k = 0; k < names.Count; k++)
                {
                    cmd.Parameters.AddWithValue("$p" + k.ToString(CultureInfo.InvariantCulture), Value(batch[i][names[k]]));
                }

                try
                {
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    outcomes[i] = new RecordOutcome(i, true);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is not (5 or 6))
                {
                    using var rollback = _c.CreateCommand();
                    rollback.Transaction = tx;
                    rollback.CommandText = "ROLLBACK TO r; RELEASE r;";
                    await rollback.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    outcomes[i] = new RecordOutcome(i, false, $"SQLite : {ex.Message}", ErrorKind.Permanent);
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new WriteResult(outcomes);
        }

        public Task CompleteAsync(CancellationToken ct) => Task.CompletedTask;

        public async ValueTask DisposeAsync() => await _c.DisposeAsync().ConfigureAwait(false);
    }
}
