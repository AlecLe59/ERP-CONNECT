using ErpConnect.Connectors.Sdk;
using Microsoft.Data.Sqlite;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

public sealed class SqliteConnectorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SqliteConnector _c = new();

    private string Db => _dir.File("erp.db");

    private ConnectorSettings Settings(int timeout = 30) => new(new Dictionary<string, string?> { ["path"] = Db, ["timeoutSeconds"] = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private void Exec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={Db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var c = new SqliteConnection($"Data Source={Db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public SqliteConnectorTests()
    {
        Exec("CREATE TABLE client (id INTEGER PRIMARY KEY, nom TEXT NOT NULL, montant REAL, actif INTEGER);");
        Exec("WITH RECURSIVE n(v) AS (SELECT 1 UNION ALL SELECT v + 1 FROM n WHERE v < 25) INSERT INTO client (id, nom, montant, actif) SELECT v, 'Client ' || v, v * 1.5, v % 2 FROM n;");
    }

    public void Dispose() => _dir.Dispose();

    private async Task<List<RecordBatch>> ReadAll(string resource, int batch, string? cursor = null)
    {
        var list = new List<RecordBatch>();
        await using var r = _c.CreateReader(Settings(), new ReadRequest(resource, batch));
        await foreach (var b in r.ReadAsync(cursor, CancellationToken.None))
        {
            list.Add(b);
        }

        return list;
    }

    [Fact]
    public async Task Reads_a_table_by_batches_with_typed_values_and_exact_resume()
    {
        var batches = await ReadAll("client", 10);
        batches.Select(b => b.Records.Count).ShouldBe([10, 10, 5]);
        var first = batches[0].Records[0];
        first.Names.ShouldBe(["id", "nom", "montant", "actif"], "la colonne technique rowid n'est pas exposée");
        (first["id"], first["nom"], first["montant"]).ShouldBe((1L, "Client 1", 1.5));
        var rest = await ReadAll("client", 10, batches[0].Cursor);
        rest.SelectMany(b => b.Records).Select(r => (long)r["id"]!).ShouldBe(Enumerable.Range(11, 15).Select(i => (long)i));
    }

    [Fact]
    public async Task Reads_a_select_query_with_offset_paging()
    {
        var batches = await ReadAll("SELECT id, upper(nom) AS nom FROM client WHERE actif = 1 ORDER BY id DESC;", 5);
        var ids = batches.SelectMany(b => b.Records).Select(r => (long)r["id"]!).ToList();
        ids.ShouldBe([25, 23, 21, 19, 17, 15, 13, 11, 9, 7, 5, 3, 1]);
        (await ReadAll("SELECT id FROM client WHERE actif = 1 ORDER BY id DESC", 5, batches[1].Cursor)).SelectMany(b => b.Records).Count().ShouldBe(3);
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE client")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM client")]
    public async Task Reading_can_never_modify_the_database(string sql)
    {
        await Should.ThrowAsync<ConnectorException>(() => ReadAll(sql, 10));
        Scalar("SELECT count(*) FROM client").ShouldBe(25);
    }

    [Fact]
    public async Task Table_names_are_escaped_not_injected()
    {
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll("client\" WHERE 1=1; DROP TABLE client; --", 10));
        ex.Kind.ShouldBe(ErrorKind.Permanent);
        Scalar("SELECT count(*) FROM client").ShouldBe(25);
    }

    [Fact]
    public async Task Insert_rejects_only_the_row_violating_a_constraint()
    {
        await using (var w = _c.CreateWriter(Settings(), new WriteRequest("client")))
        {
            var r = await w.WriteBatchAsync(
            [
                new Record().Set("id", 100L).Set("nom", "Nouveau").Set("montant", 12.34m).Set("actif", true),
                new Record().Set("id", 1L).Set("nom", "Doublon de clé"),
                new Record().Set("id", 101L).Set("nom", null),
                new Record().Set("id", 102L).Set("nom", "Autre"),
            ], default);
            r.Outcomes.Select(o => o.Success).ShouldBe([true, false, false, true]);
            r.Outcomes[1].Kind.ShouldBe(ErrorKind.Permanent);
            r.Outcomes[1].Error!.ShouldContain("UNIQUE");
            await w.CompleteAsync(default);
        }

        Scalar("SELECT count(*) FROM client").ShouldBe(27);
        Scalar("SELECT actif FROM client WHERE id = 100").ShouldBe(1);
    }

    [Fact]
    public async Task Upsert_replaces_existing_rows()
    {
        await using var w = _c.CreateWriter(Settings(), new WriteRequest("client", Options: new Dictionary<string, string> { ["mode"] = "upsert" }));
        (await w.WriteBatchAsync([new Record().Set("id", 1L).Set("nom", "Renommé")], default)).Succeeded.ShouldBe(1);
        Scalar("SELECT count(*) FROM client WHERE nom = 'Renommé'").ShouldBe(1);
        Scalar("SELECT count(*) FROM client").ShouldBe(25);
    }

    [Fact]
    public async Task Malicious_column_names_cannot_inject_sql()
    {
        await using var w = _c.CreateWriter(Settings(), new WriteRequest("client"));
        var r = await w.WriteBatchAsync([new Record().Set("id", 200L).Set("nom\") VALUES (1); DROP TABLE client; --", "x")], default);
        r.Outcomes[0].Success.ShouldBeFalse("colonne inconnue");
        Scalar("SELECT count(*) FROM client").ShouldBe(25);
    }

    [Fact]
    public async Task A_locked_database_is_a_transient_error()
    {
        using var locker = new SqliteConnection($"Data Source={Db};Pooling=False");
        locker.Open();
        using (var cmd = locker.CreateCommand())
        {
            cmd.CommandText = "BEGIN EXCLUSIVE;";
            cmd.ExecuteNonQuery();
        }

        await using var w = _c.CreateWriter(Settings(timeout: 1), new WriteRequest("client"));
        var ex = await Should.ThrowAsync<ConnectorException>(() => w.WriteBatchAsync([new Record().Set("id", 300L).Set("nom", "x")], default));
        ex.Kind.ShouldBe(ErrorKind.Transient);
    }

    [Fact]
    public async Task Discover_lists_tables_and_columns_and_test_reports_success()
    {
        var s = await _c.DiscoverAsync(Settings(), "client", default);
        s.Resources.ShouldBe(["client"]);
        s.Fields.Select(f => (f.Name, f.Type)).ShouldBe([("id", "INTEGER"), ("nom", "TEXT"), ("montant", "REAL"), ("actif", "INTEGER")]);
        (await _c.TestAsync(Settings(), default)).Success.ShouldBeTrue();
        (await _c.TestAsync(new ConnectorSettings(new Dictionary<string, string?> { ["path"] = _dir.File("absente.db") }), default)).Success.ShouldBeFalse();
    }
}
