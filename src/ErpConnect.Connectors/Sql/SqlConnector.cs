using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors.Sql;

/// <summary>
/// Connecteur de base de données commun à tous les moteurs. Lecture :
/// <list type="bullet">
/// <item>d'une table, triée par sa clé primaire (ou la colonne donnée en option « key »), reprise exacte après la dernière clé lue ;</item>
/// <item>d'une requête SELECT unique, reprise en sautant les lignes déjà lues (la requête doit avoir un ORDER BY stable).</item>
/// </list>
/// Toute lecture se fait dans une transaction qui est toujours annulée, en lecture seule quand le moteur le permet.
/// La vraie garantie reste un compte de base de données sans droit d'écriture, recommandé pour toute source.
/// Écriture : insertion ou mise à jour (« upsert » selon la clé), un point de sauvegarde par enregistrement pour qu'une
/// ligne refusée n'annule pas les autres.
/// </summary>
public sealed class SqlConnector : IConnector
{
    private readonly SqlDialect _d;

    public SqlConnector(SqlDialect dialect)
    {
        _d = dialect ?? throw new ArgumentNullException(nameof(dialect));
        Manifest = new ConnectorManifest(
            _d.Id, _d.Name, "1.0.0",
            ConnectorCapabilities.Read | ConnectorCapabilities.Write | ConnectorCapabilities.Discover,
            [
                .. _d.Fields,
                new("timeoutSeconds", "Délai de connexion (s)", ConfigFieldType.Number, Default: "30"),
                new("commandTimeoutSeconds", "Délai maximal d'une requête (s)", ConfigFieldType.Number, Default: "600"),
            ],
            _d.Description);
    }

    public ConnectorManifest Manifest { get; }

    public SqlDialect Dialect => _d;

    internal static bool IsQuery(string resource)
    {
        var t = resource.TrimStart();
        return t.StartsWith("select ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("select\n", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("with ", StringComparison.OrdinalIgnoreCase) || t.StartsWith("with\n", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Une seule requête, sans point-virgule interne. Ce n'est pas une barrière de sécurité : la transaction annulée et le compte en lecture seule le sont.</summary>
    internal static string CheckQuery(string sql)
    {
        var t = sql.Trim().TrimEnd(';').Trim();
        if (t.Contains(';', StringComparison.Ordinal))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Une seule requête SELECT est autorisée.");
        }

        return t;
    }

    public async Task<DbConnection> OpenAsync(ConnectorSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var c = _d.CreateConnection(settings);
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            return c;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw _d.Map(ex, "connexion impossible : ");
        }
    }

    private static DbCommand Command(DbConnection c, DbTransaction? tx, string sql, ConnectorSettings settings)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
#pragma warning disable CA2100 // Identifiants échappés par le dialecte, valeurs toujours paramétrées ; requête de lecture définie par le concepteur du flux.
        cmd.CommandText = sql;
#pragma warning restore CA2100
        cmd.CommandTimeout = settings.GetInt("commandTimeoutSeconds", 600);
        if (cmd is Oracle.ManagedDataAccess.Client.OracleCommand oc)
        {
            oc.BindByName = true;
        }

        return cmd;
    }

    private void AddParameter(DbCommand cmd, int i, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = _d.ParameterName(i);
        p.Value = _d.ToParameter(value);
        cmd.Parameters.Add(p);
    }

    /// <summary>Ouvre la transaction de lecture : lecture seule si le moteur le permet, toujours annulée à la fin.</summary>
    private async Task<DbTransaction> BeginReadAsync(DbConnection c, ConnectorSettings settings, CancellationToken ct)
    {
        if (_d.ReadOnlyBeforeBegin is { } before)
        {
            await using var pre = Command(c, null, before, settings);
            await pre.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (_d.ReadOnlyAfterBegin is { } after)
        {
            await using var post = Command(c, tx, after, settings);
            await post.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return tx;
    }

    public async Task<ConnectionTestResult> TestAsync(ConnectorSettings settings, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var c = await OpenAsync(settings, ct).ConfigureAwait(false);
            return new ConnectionTestResult(true, $"Connexion établie ({_d.Name} {c.ServerVersion}).", sw.Elapsed);
        }
        catch (ConnectorException ex)
        {
            return new ConnectionTestResult(false, ex.Message, sw.Elapsed);
        }
    }

    public async Task<DataStructure> DiscoverAsync(ConnectorSettings settings, string? resource, CancellationToken ct)
    {
        await using var c = await OpenAsync(settings, ct).ConfigureAwait(false);
        try
        {
            var tables = new List<string>();
            if (!string.IsNullOrEmpty(_d.TablesQuery))
            {
                await using var cmd = Command(c, null, _d.TablesQuery, settings);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    tables.Add(r.GetString(0));
                }
            }
            else
            {
                using var schema = await c.GetSchemaAsync("Tables", ct).ConfigureAwait(false);
                foreach (DataRow row in schema.Rows)
                {
                    if (row["TABLE_NAME"] is string name && row["TABLE_TYPE"] is "TABLE" or "VIEW")
                    {
                        tables.Add(name);
                    }
                }
            }

            var fields = new List<FieldInfo>();
            if (!string.IsNullOrWhiteSpace(resource))
            {
                var sql = IsQuery(resource) ? CheckQuery(resource) : _d.ColumnsQuery(_d.QuoteQualified(resource));
                await using var tx = await BeginReadAsync(c, settings, ct).ConfigureAwait(false);
                await using var cmd = Command(c, tx, sql, settings);
                await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct).ConfigureAwait(false);
                for (var i = 0; i < r.FieldCount; i++)
                {
                    fields.Add(new FieldInfo(r.GetName(i), r.GetDataTypeName(i)));
                }

                await r.CloseAsync().ConfigureAwait(false);
                await tx.RollbackAsync(ct).ConfigureAwait(false);
            }

            return new DataStructure(tables, fields);
        }
        catch (DbException ex)
        {
            throw _d.Map(ex, string.Empty);
        }
    }

    /// <summary>Colonne de clé d'une table : option « key », sinon la clé primaire si elle porte sur une seule colonne.</summary>
    internal async Task<string> KeyColumnAsync(DbConnection c, ConnectorSettings settings, string table, string? option, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(option))
        {
            return option.Trim();
        }

        if (_d.PrimaryKeyQuery is { } sql)
        {
            var parts = table.Trim().Split('.');
            await using var cmd = Command(c, null, sql, settings);
            AddParameter(cmd, 0, parts[^1]);
            AddParameter(cmd, 1, parts.Length > 1 ? parts[^2] : string.Empty);
            var keys = new List<string>();
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    keys.Add(r.GetString(0));
                }
            }

            if (keys.Count == 1)
            {
                return keys[0];
            }
        }

        throw new ConnectorException(ErrorKind.Permanent,
            $"La table {table} n'a pas de clé primaire sur une seule colonne : indiquer la colonne de tri (option « key ») ou lire par une requête SELECT avec ORDER BY.");
    }

    public IRecordReader CreateReader(ConnectorSettings settings, ReadRequest request) => new Reader(this, settings, request);

    public IRecordWriter CreateWriter(ConnectorSettings settings, WriteRequest request) => new Writer(this, settings, request);

    internal static object? ReadValue(DbDataReader r, int i)
    {
        if (r.IsDBNull(i))
        {
            return null;
        }

        object v;
        try
        {
            v = r.GetValue(i);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException)
        {
            return r.GetString(i); // nombre Oracle au-delà de la précision d'un decimal .NET : gardé en texte, sans perte
        }

        return v switch
        {
            string or long or int or short or byte or decimal or double or float or bool or DateTime or DateTimeOffset or DateOnly or byte[] or Guid => v,
            sbyte sb => (long)sb,
            ushort us => (long)us,
            uint ui => (long)ui,
            ulong ul => (decimal)ul,
            TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            char ch => ch.ToString(),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Curseur de reprise : dernière clé lue (table) ou nombre de lignes lues (requête).</summary>
    private sealed record Cursor(JsonElement? Key, long Rows)
    {
        public static Cursor Parse(string? s)
        {
            if (s is null)
            {
                return new Cursor(null, 0);
            }

            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                return new Cursor(root.TryGetProperty("k", out var k) ? k.Clone() : null, root.TryGetProperty("n", out var n) ? n.GetInt64() : 0);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                throw new ConnectorException(ErrorKind.Permanent, "Point de reprise illisible.", ex);
            }
        }

        public static string ForKey(object? key, long rows)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WritePropertyName("k");
                switch (key)
                {
                    case long or int or short or byte or sbyte or ushort or uint:
                        w.WriteNumberValue(Convert.ToInt64(key, CultureInfo.InvariantCulture));
                        break;
                    case decimal d:
                        w.WriteStartObject();
                        w.WriteString("d", d.ToString(CultureInfo.InvariantCulture));
                        w.WriteEndObject();
                        break;
                    case DateTime dt:
                        w.WriteStartObject();
                        w.WriteString("t", dt.ToString("O", CultureInfo.InvariantCulture));
                        w.WriteEndObject();
                        break;
                    case Guid g:
                        w.WriteStartObject();
                        w.WriteString("g", g.ToString());
                        w.WriteEndObject();
                        break;
                    default:
                        w.WriteStringValue(Convert.ToString(key, CultureInfo.InvariantCulture));
                        break;
                }

                w.WriteNumber("n", rows);
                w.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        public static string ForRows(long rows) => "{\"n\":" + rows.ToString(CultureInfo.InvariantCulture) + "}";

        public object? KeyValue => Key switch
        {
            null => null,
            { ValueKind: JsonValueKind.Number } k => k.GetInt64(),
            { ValueKind: JsonValueKind.String } k => k.GetString(),
            { ValueKind: JsonValueKind.Object } k when k.TryGetProperty("d", out var d) => decimal.Parse(d.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
            { ValueKind: JsonValueKind.Object } k when k.TryGetProperty("t", out var t) => DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            { ValueKind: JsonValueKind.Object } k when k.TryGetProperty("g", out var g) => Guid.Parse(g.GetString()!),
            _ => throw new ConnectorException(ErrorKind.Permanent, "Point de reprise illisible."),
        };
    }

    private sealed class Reader(SqlConnector owner, ConnectorSettings settings, ReadRequest request) : IRecordReader
    {
        public async IAsyncEnumerable<RecordBatch> ReadAsync(string? cursor, [EnumeratorCancellation] CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            var d = owner._d;
            var start = Cursor.Parse(cursor);
            var query = IsQuery(request.Resource);
            await using var c = await owner.OpenAsync(settings, ct).ConfigureAwait(false);
            DbTransaction tx;
            DbCommand cmd;
            string? keyName = null;
            try
            {
                string sql;
                if (query)
                {
                    sql = CheckQuery(request.Resource);
                }
                else
                {
                    keyName = await owner.KeyColumnAsync(c, settings, request.Resource, request.Option("key"), ct).ConfigureAwait(false);
                    sql = d.KeysetQuery(d.QuoteQualified(request.Resource), d.QuoteName(keyName), start.Key is not null);
                }

                tx = await owner.BeginReadAsync(c, settings, ct).ConfigureAwait(false);
                cmd = Command(c, tx, sql, settings);
                if (!query && start.KeyValue is { } k)
                {
                    owner.AddParameter(cmd, 0, k);
                }
            }
            catch (DbException ex)
            {
                throw d.Map(ex, string.Empty);
            }

            await using (tx)
            await using (cmd)
            {
                DbDataReader r;
                try
                {
                    r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
                }
                catch (DbException ex)
                {
                    throw d.Map(ex, string.Empty);
                }

                await using (r)
                {
                    var names = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
                    var keyIndex = keyName is null ? -1 : Array.FindIndex(names, n => string.Equals(n, keyName, StringComparison.OrdinalIgnoreCase));
                    if (!query && keyIndex < 0)
                    {
                        throw new ConnectorException(ErrorKind.Permanent, $"Colonne de clé introuvable : {keyName}.");
                    }

                    var rows = start.Rows;
                    var skip = query ? start.Rows : 0;
                    object? lastKey = null;
                    var batch = new List<Record>(request.BatchSize);
                    while (true)
                    {
                        bool more;
                        try
                        {
                            more = await r.ReadAsync(ct).ConfigureAwait(false);
                        }
                        catch (DbException ex)
                        {
                            throw d.Map(ex, string.Empty);
                        }

                        if (!more)
                        {
                            break;
                        }

                        if (skip > 0)
                        {
                            skip--;
                            continue;
                        }

                        var rec = new Record();
                        for (var i = 0; i < names.Length; i++)
                        {
                            var v = ReadValue(r, i);
                            if (i == keyIndex)
                            {
                                lastKey = v;
                            }

                            rec.Set(names[i], v);
                        }

                        batch.Add(rec);
                        rows++;
                        if (batch.Count == request.BatchSize)
                        {
                            yield return new RecordBatch(batch, query ? Cursor.ForRows(rows) : Cursor.ForKey(lastKey, rows));
                            batch = new List<Record>(request.BatchSize);
                        }
                    }

                    if (batch.Count > 0)
                    {
                        yield return new RecordBatch(batch, query ? Cursor.ForRows(rows) : Cursor.ForKey(lastKey, rows));
                    }
                }

                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Writer : IRecordWriter
    {
        private readonly SqlConnector _owner;
        private readonly ConnectorSettings _settings;
        private readonly string _table;
        private readonly bool _upsert;
        private readonly IReadOnlyList<string> _keys;
        private DbConnection? _c;

        public Writer(SqlConnector owner, ConnectorSettings settings, WriteRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            _owner = owner;
            _settings = settings;
            var d = owner._d;
            _table = d.QuoteQualified(request.Resource);
            _upsert = (request.Option("mode") ?? "insert") switch
            {
                "insert" => false,
                "upsert" => true,
                _ => throw new ConnectorException(ErrorKind.Permanent, "Mode d'écriture : insert ou upsert."),
            };
            _keys = (request.Option("key") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (_upsert && _keys.Count == 0)
            {
                throw new ConnectorException(ErrorKind.Permanent, "La mise à jour (upsert) exige la ou les colonnes de clé (option « key »).");
            }

            if (_upsert && !d.SupportsSavepoints)
            {
                d.Upsert(_table, [], []); // lève l'erreur explicite du dialecte
            }
        }

        private string Sql(IReadOnlyList<string> names)
        {
            var d = _owner._d;
            var cols = names.Select(d.QuoteName).ToList();
            if (!_upsert)
            {
                return d.Insert(_table, cols);
            }

            var missing = _keys.Where(k => !names.Contains(k, StringComparer.Ordinal)).ToList();
            if (missing.Count > 0)
            {
                throw new ConnectorException(ErrorKind.Permanent, $"Colonne de clé absente de l'enregistrement : {string.Join(", ", missing)}.");
            }

            return d.Upsert(_table, cols, _keys.Select(d.QuoteName).ToList());
        }

        private async Task ExecAsync(DbTransaction? tx, string sql, CancellationToken ct)
        {
            await using var cmd = Command(_c!, tx, sql, _settings);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<WriteResult> WriteBatchAsync(IReadOnlyList<Record> batch, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(batch);
            _c ??= await _owner.OpenAsync(_settings, ct).ConfigureAwait(false);
            if (batch.Count > 1 && _owner._d.SupportsSavepoints && _c.CanCreateBatch && await TryWholeBatchAsync(batch, ct).ConfigureAwait(false))
            {
                return WriteResult.AllSucceeded(batch.Count);
            }

            return await RowByRowAsync(batch, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Voie rapide : tout le lot en un seul aller-retour, dans une transaction. Au moindre refus, la transaction est annulée
        /// et le lot repris ligne par ligne (voie lente), pour ne rejeter que les lignes fautives.
        /// </summary>
        private async Task<bool> TryWholeBatchAsync(IReadOnlyList<Record> batch, CancellationToken ct)
        {
            var d = _owner._d;
            await using var db = _c!.CreateBatch();
            foreach (var rec in batch)
            {
                var names = rec.Names.ToList();
                var cmd = db.CreateBatchCommand();
                try
                {
                    cmd.CommandText = Sql(names);
                }
                catch (ConnectorException)
                {
                    return false;
                }

                for (var k = 0; k < names.Count; k++)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = d.ParameterName(k);
                    p.Value = d.ToParameter(rec[names[k]]);
                    cmd.Parameters.Add(p);
                }

                db.BatchCommands.Add(cmd);
            }

            db.Timeout = _settings.GetInt("commandTimeoutSeconds", 600);
            await using var tx = await _c.BeginTransactionAsync(ct).ConfigureAwait(false);
            db.Transaction = tx;
            try
            {
                await db.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (DbException ex) when (!ex.IsTransient && _c.State == ConnectionState.Open)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
            catch (DbException ex)
            {
                await ResetAsync().ConfigureAwait(false);
                throw d.Map(ex, "lot annulé : ");
            }
        }

        private async Task<WriteResult> RowByRowAsync(IReadOnlyList<Record> batch, CancellationToken ct)
        {
            var d = _owner._d;
            _c ??= await _owner.OpenAsync(_settings, ct).ConfigureAwait(false);
            var outcomes = new RecordOutcome[batch.Count];
            DbTransaction? tx = null;
            try
            {
                if (d.SupportsSavepoints)
                {
                    tx = await _c.BeginTransactionAsync(ct).ConfigureAwait(false);
                }

                for (var i = 0; i < batch.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var names = batch[i].Names.ToList();
                    string sql;
                    try
                    {
                        sql = Sql(names);
                    }
                    catch (ConnectorException ex)
                    {
                        outcomes[i] = new RecordOutcome(i, false, ex.Message, ErrorKind.Permanent);
                        continue;
                    }

                    if (tx is not null)
                    {
                        await ExecAsync(tx, d.Savepoint, ct).ConfigureAwait(false);
                    }

                    await using var cmd = Command(_c, tx, sql, _settings);
                    for (var k = 0; k < names.Count; k++)
                    {
                        _owner.AddParameter(cmd, k, batch[i][names[k]]);
                    }

                    try
                    {
                        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        if (tx is not null && d.ReleaseSavepoint is { } release)
                        {
                            await ExecAsync(tx, release, ct).ConfigureAwait(false);
                        }

                        outcomes[i] = new RecordOutcome(i, true);
                    }
                    catch (DbException ex) when (!ex.IsTransient && _c.State == ConnectionState.Open)
                    {
                        // Contrainte, type, droit : la ligne est rejetée, les autres continuent.
                        if (tx is not null)
                        {
                            await ExecAsync(tx, d.RollbackToSavepoint, ct).ConfigureAwait(false);
                        }

                        outcomes[i] = new RecordOutcome(i, false, d.Map(ex, string.Empty).Message, ErrorKind.Permanent);
                    }
                }

                if (tx is not null)
                {
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }

                return new WriteResult(outcomes);
            }
            catch (DbException ex)
            {
                // Transaction annulée en entier (panne réseau, interblocage, savepoint impossible) : rien n'est écrit pour ce lot.
                await ResetAsync().ConfigureAwait(false);
                throw d.Map(ex, "lot annulé : ");
            }
            finally
            {
                if (tx is not null)
                {
                    await tx.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task ResetAsync()
        {
            if (_c is not null)
            {
                await _c.DisposeAsync().ConfigureAwait(false);
                _c = null;
            }
        }

        public Task CompleteAsync(CancellationToken ct) => Task.CompletedTask;

        public async ValueTask DisposeAsync() => await ResetAsync().ConfigureAwait(false);
    }
}
