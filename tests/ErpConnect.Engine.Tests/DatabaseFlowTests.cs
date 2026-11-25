using System.Globalization;
using System.Text.Json;
using ErpConnect.Connectors.Sql;
using ErpConnect.Connectors.Tests;
using ErpConnect.Core.Runs;
using ErpConnect.Flows;
using Xunit.Abstractions;

namespace ErpConnect.Engine.Tests;

/// <summary>
/// Flux complets par le moteur (connexions enregistrées, secret dans le coffre, exécution, rejets) vers et depuis de vrais
/// serveurs de base de données. Serveurs fournis par variables d'environnement (voir DatabaseFact).
/// </summary>
[Trait("Category", "Database")]
public sealed class DatabaseFlowTests(ITestOutputHelper output) : IAsyncLifetime
{
    private Kernel _k = null!;

    public async Task InitializeAsync() => _k = await Kernel.StartAsync();

    public async Task DisposeAsync() => await _k.DisposeAsync();

    private static readonly Dictionary<string, (SqlDialect Dialect, string Id)> Engines = new()
    {
        ["ERPCONNECT_TEST_POSTGRESQL"] = (new PostgreSqlDialect(), "db.postgresql"),
        ["ERPCONNECT_TEST_SQLSERVER"] = (new SqlServerDialect(), "db.sqlserver"),
        ["ERPCONNECT_TEST_MYSQL"] = (new MySqlDialect(), "db.mysql"),
        ["ERPCONNECT_TEST_ORACLE"] = (new OracleDialect(), "db.oracle"),
    };

    private static async Task Exec(string variable, string sql)
    {
        var c = new SqlConnector(Engines[variable].Dialect);
        await using var conn = await c.OpenAsync(DatabaseTestServer.Settings(variable), CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SaveConnection(string variable)
    {
        var (values, secrets) = DatabaseTestServer.Read(variable);
        await _k.Connections.SaveAsync(Kernel.Admin, "base", "base", Engines[variable].Id, values,
            secrets.ToDictionary(kv => kv.Key, kv => (string?)kv.Value), [], 0, "t", default);
    }

    private async Task Csv_to_database_upsert_twice_then_database_to_json(string variable)
    {
        var d = Engines[variable].Dialect;
        var table = "flux_" + Guid.NewGuid().ToString("N")[..8];
        var text = d is OracleDialect ? "VARCHAR2(100 CHAR)" : d is SqlServerDialect ? "NVARCHAR(100)" : "VARCHAR(100)";
        var number = d is OracleDialect ? "NUMBER(12,2)" : "DECIMAL(12,2)";
        await Exec(variable, $"CREATE TABLE {d.QuoteName(table)} ({d.QuoteName("code")} {text} NOT NULL PRIMARY KEY, {d.QuoteName("nom")} {text} NOT NULL, {d.QuoteName("montant")} {number})");
        try
        {
            var input = _k.Folder("in");
            var outDir = _k.Folder("out");
            await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 1200);
            await _k.LocalAsync("src", input);
            await _k.LocalAsync("dst", outDir);
            await SaveConnection(variable);
            FlowDefinition Load() => new(
                new SourceSpec("src", "*.csv", [new("format", "csv")]),
                new TargetSpec("base", table, "createNew", [new("mode", "upsert"), new("key", "code")]),
                new MappingSpec("explicit", [new("code", "padLeft([id], 5, '0')"), new("nom", "[nom]"), new("montant", "toNumber([montant])")]), 250);

            var first = await _k.FlowRunAsync(Load());
            first.ShouldHaveStatus(RunStatus.Succeeded);
            first.WrittenCount.ShouldBe(1200);
            var again = await _k.FlowRunAsync(Load());
            again.ShouldHaveStatus(RunStatus.Succeeded);
            again.WrittenCount.ShouldBe(1200, "relancer le même fichier met à jour sans créer de doublon");

            var export = await _k.FlowRunAsync(new FlowDefinition(
                new SourceSpec("base", $"SELECT {d.QuoteName("code")}, {d.QuoteName("montant")} FROM {d.QuoteName(table)} WHERE {d.QuoteName("montant")} >= 100 ORDER BY {d.QuoteName("code")}", []),
                new TargetSpec("dst", "export.json", "createNew", [new("format", "json")]),
                new MappingSpec("identity", []), 7));
            export.ShouldHaveStatus(RunStatus.Succeeded);
            var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "export.json"))).RootElement;
            json.GetArrayLength().ShouldBe(1100);
            json[0].GetProperty("code").GetString().ShouldBe("00100");
            json[0].GetProperty("montant").GetDecimal().ShouldBe(100.5m);

            var table2 = await _k.FlowRunAsync(new FlowDefinition(
                new SourceSpec("base", table, []),
                new TargetSpec("dst", "table.csv", "createNew", [new("format", "csv")]),
                new MappingSpec("identity", []), 100));
            table2.ShouldHaveStatus(RunStatus.Succeeded);
            table2.ReadCount.ShouldBe(1200);
        }
        finally
        {
            await Exec(variable, $"DROP TABLE {d.QuoteName(table)}");
        }
    }

    [DatabaseFact("ERPCONNECT_TEST_POSTGRESQL")] public Task PostgreSql() => Csv_to_database_upsert_twice_then_database_to_json("ERPCONNECT_TEST_POSTGRESQL");

    [DatabaseFact("ERPCONNECT_TEST_SQLSERVER")] public Task SqlServer() => Csv_to_database_upsert_twice_then_database_to_json("ERPCONNECT_TEST_SQLSERVER");

    [DatabaseFact("ERPCONNECT_TEST_MYSQL")] public Task MySql() => Csv_to_database_upsert_twice_then_database_to_json("ERPCONNECT_TEST_MYSQL");

    [DatabaseFact("ERPCONNECT_TEST_ORACLE")] public Task Oracle() => Csv_to_database_upsert_twice_then_database_to_json("ERPCONNECT_TEST_ORACLE");

    /// <summary>Mesure : un million de lignes lues dans PostgreSQL et écrites en CSV, puis 100 000 lignes CSV insérées.</summary>
    [DatabaseFact("ERPCONNECT_TEST_POSTGRESQL")]
    [Trait("Category", "Performance")]
    public async Task PostgreSql_one_million_rows_to_csv_and_100k_rows_back()
    {
        const string v = "ERPCONNECT_TEST_POSTGRESQL";
        var table = "perf_" + Guid.NewGuid().ToString("N")[..8];
        await Exec(v, $"CREATE TABLE {table} (id bigint PRIMARY KEY, nom text NOT NULL, montant numeric(12,2), cree date)");
        await Exec(v, $"INSERT INTO {table} SELECT g, 'Client ' || g, g * 1.5, DATE '2026-01-01' + (g % 365) FROM generate_series(1, 1000000) g");
        try
        {
            var outDir = _k.Folder("out");
            await _k.LocalAsync("dst", outDir);
            await SaveConnection(v);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var read = await _k.FlowRunAsync(new FlowDefinition(new SourceSpec("base", table, []),
                new TargetSpec("dst", "big.csv", "createNew", [new("format", "csv")]), new MappingSpec("identity", []), 1000));
            var readTime = sw.Elapsed;
            read.ShouldHaveStatus(RunStatus.Succeeded);
            read.WrittenCount.ShouldBe(1_000_000);

            var input = _k.Folder("in");
            await Kernel.WriteCsvAsync(Path.Combine(input, "a.csv"), 100_000);
            await _k.LocalAsync("src", input);
            await Exec(v, $"CREATE TABLE {table}_in (code text PRIMARY KEY, nom text NOT NULL, montant numeric(12,2))");
            sw.Restart();
            var write = await _k.FlowRunAsync(new FlowDefinition(new SourceSpec("src", "*.csv", [new("format", "csv")]),
                new TargetSpec("base", table + "_in", "createNew", []),
                new MappingSpec("explicit", [new("code", "[id]"), new("nom", "[nom]"), new("montant", "toNumber([montant])")]), 1000));
            var writeTime = sw.Elapsed;
            write.ShouldHaveStatus(RunStatus.Succeeded);
            write.WrittenCount.ShouldBe(100_000);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PostgreSQL→CSV 1 M lignes : {readTime.TotalSeconds:F1} s ; CSV→PostgreSQL 100 000 lignes : {writeTime.TotalSeconds:F1} s"));
        }
        finally
        {
            await Exec(v, $"DROP TABLE IF EXISTS {table}_in");
            await Exec(v, $"DROP TABLE {table}");
        }
    }
}
