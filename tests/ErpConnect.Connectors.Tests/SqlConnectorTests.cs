using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using ErpConnect.Connectors.Sdk;
using ErpConnect.Connectors.Sql;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

/// <summary>Conformance commune à tous les moteurs, exécutée contre un vrai serveur.</summary>
[Trait("Category", "Database")]
public abstract class SqlConformanceTests : IAsyncLifetime
{
    protected SqlConformanceTests(SqlDialect dialect, string variable)
    {
        Dialect = dialect;
        Connector = new SqlConnector(dialect);
        Variable = variable;
        Table = "t_" + Guid.NewGuid().ToString("N")[..10];
    }

    protected SqlDialect Dialect { get; }

    protected SqlConnector Connector { get; }

    protected string Variable { get; }

    /// <summary>Table propre au test (nom aléatoire), supprimée à la fin.</summary>
    protected string Table { get; }

    protected string Q(string name) => Dialect.QuoteName(name);

    protected virtual string IntType => "INT";

    protected virtual string TextType => "VARCHAR(100)";

    protected virtual string DecimalType => "DECIMAL(12,2)";

    protected virtual string BoolType => "BOOLEAN";

    protected virtual string DateType => "DATE";

    protected virtual string Bool(bool b) => b ? "TRUE" : "FALSE";

    protected virtual string Date(string iso) => $"DATE '{iso}'";

    /// <summary>Instruction de lecture qui tenterait d'écrire : doit échouer ou être annulée.</summary>
    protected virtual IEnumerable<string> WritingSelects =>
    [
        $"WITH x AS (SELECT 1 AS a) SELECT * FROM x; DELETE FROM {Q(Table)}",
        $"SELECT 1 AS a; DROP TABLE {Q(Table)}",
    ];

    protected ConnectorSettings Settings(Action<Dictionary<string, string?>>? change = null)
    {
        var (values, secrets) = DatabaseTestServer.Read(Variable);
        change?.Invoke(values);
        if (values.Remove("__password", out var pw))
        {
            secrets["password"] = pw!;
        }

        return new ConnectorSettings(values, secrets);
    }

    protected async Task Exec(string sql)
    {
        await using var c = await Connector.OpenAsync(Settings(), CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    protected async Task<long> Count(string table)
    {
        await using var c = await Connector.OpenAsync(Settings(), CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Q(table)}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    protected async Task<bool> TableExists(string table)
    {
        var s = await Connector.DiscoverAsync(Settings(), null, CancellationToken.None);
        return s.Resources.Contains(table, StringComparer.OrdinalIgnoreCase);
    }

    protected static long N(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    protected static decimal D(object? v) => Convert.ToDecimal(v, CultureInfo.InvariantCulture);

    protected static bool B(object? v) => v is bool b ? b : Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0;

    public virtual async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(Variable) is null)
        {
            return; // test ignoré (voir DatabaseFact)
        }

        await Exec($"CREATE TABLE {Q(Table)} ({Q("id")} {IntType} NOT NULL PRIMARY KEY, {Q("nom")} {TextType} NOT NULL, {Q("montant")} {DecimalType}, {Q("actif")} {BoolType}, {Q("cree")} {DateType})");
        var rows = Enumerable.Range(1, 25).Select(i =>
            $"({i}, 'Client {i}', {(i * 1.5m).ToString(CultureInfo.InvariantCulture)}, {Bool(i % 2 == 1)}, {Date($"2026-01-{i:00}")})");
        await Exec($"INSERT INTO {Q(Table)} ({Q("id")}, {Q("nom")}, {Q("montant")}, {Q("actif")}, {Q("cree")}) VALUES {string.Join(", ", rows)}");
        await AfterDdl(Table);
    }

    public virtual async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable(Variable) is null)
        {
            return;
        }

        foreach (var t in new[] { Table, Table + "_sanscle", Table + "_copie" })
        {
            try
            {
                await Exec($"DROP TABLE {Q(t)}");
            }
            catch (Exception ex) when (ex is DbException or ConnectorException)
            {
                // table jamais créée
            }
        }
    }

    protected async Task<List<RecordBatch>> ReadAll(string resource, int batch, string? cursor = null, Dictionary<string, string>? options = null)
    {
        var list = new List<RecordBatch>();
        await using var r = Connector.CreateReader(Settings(), new ReadRequest(resource, batch, options));
        await foreach (var b in r.ReadAsync(cursor, CancellationToken.None))
        {
            list.Add(b);
        }

        return list;
    }

    protected async Task<WriteResult> Write(string table, IReadOnlyList<Record> records, string mode = "insert", string? key = null)
    {
        var options = new Dictionary<string, string> { ["mode"] = mode };
        if (key is not null)
        {
            options["key"] = key;
        }

        await using var w = Connector.CreateWriter(Settings(), new WriteRequest(table, WriteMode.Overwrite, options));
        var result = await w.WriteBatchAsync(records, CancellationToken.None);
        await w.CompleteAsync(CancellationToken.None);
        return result;
    }

    protected Task Run(Func<Task> test) => test();

    /// <summary>Appelé après chaque création de table par les tests.</summary>
    protected virtual Task AfterDdl(string table) => Task.CompletedTask;
}

/// <summary>Cas communs, déclarés une fois par moteur dans les classes concrètes (pour l'attribut de saut).</summary>
public abstract class SqlConformanceCases(SqlDialect dialect, string variable) : SqlConformanceTests(dialect, variable)
{
    protected async Task Connection_test_reports_success_then_a_wrong_password_as_permanent()
    {
        var ok = await Connector.TestAsync(Settings(), CancellationToken.None);
        ok.Success.ShouldBeTrue(ok.Message);
        var bad = Settings(v => v["__password"] = "mauvais-" + Guid.NewGuid().ToString("N"));
        (await Connector.TestAsync(bad, CancellationToken.None)).Success.ShouldBeFalse();
        var ex2 = await Should.ThrowAsync<ConnectorException>(async () =>
        {
            await using var r = Connector.CreateReader(bad, new ReadRequest(Table, 10));
            await foreach (var b in r.ReadAsync(null, CancellationToken.None))
            {
                _ = b;
            }
        });
        ex2.Kind.ShouldBe(ErrorKind.Permanent, ex2.Message);
        ex2.Message.ShouldNotContain(Settings().GetSecret("password")!);
    }

    protected async Task Unreachable_server_is_a_transient_error(ErrorKind expected = ErrorKind.Transient)
    {
        var settings = Settings(v =>
        {
            v["port"] = "1";
            v["timeoutSeconds"] = "5";
            if (v.TryGetValue("connectionString", out var cs) && cs is not null)
            {
                v["connectionString"] = System.Text.RegularExpressions.Regex.Replace(cs, "Port=\\d+", "Port=1");
            }
        });
        var ex = await Should.ThrowAsync<ConnectorException>(async () =>
        {
            await using var r = Connector.CreateReader(settings, new ReadRequest(Table, 10));
            await foreach (var b in r.ReadAsync(null, CancellationToken.None))
            {
                _ = b;
            }
        });
        ex.Kind.ShouldBe(expected, ex.Message);
        ex.Message.ShouldContain("connexion impossible");
    }

    protected async Task Discovery_lists_the_table_and_its_columns()
    {
        var s = await Connector.DiscoverAsync(Settings(), Table, CancellationToken.None);
        s.Resources.ShouldContain(t => string.Equals(t, Table, StringComparison.OrdinalIgnoreCase));
        s.Fields.Select(f => f.Name.ToLowerInvariant()).ShouldBe(["id", "nom", "montant", "actif", "cree"]);
    }

    protected async Task Reads_a_table_by_batches_with_typed_values_and_exact_resume()
    {
        var batches = await ReadAll(Table, 10);
        batches.Select(b => b.Records.Count).ShouldBe([10, 10, 5]);
        var first = batches[0].Records[0];
        first.Names.Select(n => n.ToLowerInvariant()).ShouldBe(["id", "nom", "montant", "actif", "cree"]);
        N(first.Names.Select(n => first[n]).First()).ShouldBe(1);
        first[first.Names.ElementAt(1)].ShouldBe("Client 1");
        D(first[first.Names.ElementAt(2)]).ShouldBe(1.5m);
        B(first[first.Names.ElementAt(3)]).ShouldBeTrue();
        var cree = first[first.Names.ElementAt(4)];
        (cree switch { DateTimeOffset dto => DateOnly.FromDateTime(dto.DateTime), DateOnly d => d, _ => throw new InvalidOperationException(cree?.GetType().Name) })
            .ShouldBe(new DateOnly(2026, 1, 1));
        var rest = await ReadAll(Table, 10, batches[0].Cursor);
        rest.SelectMany(b => b.Records).Select(r => N(r[r.Names.First()])).ShouldBe(Enumerable.Range(11, 15).Select(i => (long)i));
    }

    protected async Task Reads_a_select_query_and_resumes_after_the_rows_already_read()
    {
        var sql = $"SELECT {Q("id")}, {Q("nom")} FROM {Q(Table)} WHERE {Q("id")} > 5 ORDER BY {Q("id")} DESC";
        var batches = await ReadAll(sql, 6);
        batches.SelectMany(b => b.Records).Select(r => N(r[r.Names.First()])).ShouldBe(Enumerable.Range(6, 20).Reverse().Select(i => (long)i));
        (await ReadAll(sql, 6, batches[1].Cursor)).SelectMany(b => b.Records).Select(r => N(r[r.Names.First()])).ShouldBe([13L, 12, 11, 10, 9, 8, 7, 6]);
    }

    protected async Task Reading_can_never_modify_the_database()
    {
        foreach (var sql in WritingSelects)
        {
            try
            {
                await ReadAll(sql, 10);
            }
            catch (ConnectorException)
            {
                // refus attendu, ou écriture annulée : dans les deux cas rien ne doit avoir changé
            }

            (await Count(Table)).ShouldBe(25, sql);
        }

        (await TableExists(Table + "_copie")).ShouldBeFalse();
    }

    protected async Task Table_names_are_escaped_and_never_executed()
    {
        var hostile = Table + "\"; DROP TABLE " + Table + "; --";
        await Should.ThrowAsync<ConnectorException>(() => ReadAll(hostile, 10, options: new() { ["key"] = "id" }));
        (await Count(Table)).ShouldBe(25);
    }

    protected async Task A_table_without_primary_key_needs_a_key_column()
    {
        var t = Table + "_sanscle";
        await Exec($"CREATE TABLE {Q(t)} ({Q("code")} {TextType} NOT NULL, {Q("libelle")} {TextType})");
        await Exec($"INSERT INTO {Q(t)} ({Q("code")}, {Q("libelle")}) VALUES ('B', 'deux'), ('A', 'un'), ('C', 'trois')");
        await AfterDdl(t);
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(t, 10));
        ex.Message.ShouldContain("key");
        var batches = await ReadAll(t, 2, options: new() { ["key"] = "code" });
        batches.SelectMany(b => b.Records).Select(r => r[r.Names.First()]).ShouldBe(["A", "B", "C"]);
        (await ReadAll(t, 2, batches[0].Cursor, new() { ["key"] = "code" })).SelectMany(b => b.Records).Select(r => r[r.Names.First()]).ShouldBe(["C"]);
    }

    protected async Task Insert_rejects_only_the_faulty_rows()
    {
        var result = await Write(Table,
        [
            Record.From(("id", 100L), ("nom", "Nouveau"), ("montant", 12.34m), ("actif", true), ("cree", new DateOnly(2026, 2, 1))),
            Record.From(("id", 1L), ("nom", "Doublon de clé")),
            Record.From(("id", 101L), ("nom", null)),
            Record.From(("id", 102L), ("nom", "Accentué é ü 日本"), ("montant", null)),
        ]);
        result.Outcomes.Select(o => o.Success).ShouldBe([true, false, false, true]);
        result.Outcomes[1].Kind.ShouldBe(ErrorKind.Permanent);
        (await Count(Table)).ShouldBe(27);
        var rows = (await ReadAll($"SELECT {Q("nom")}, {Q("montant")} FROM {Q(Table)} WHERE {Q("id")} >= 100 ORDER BY {Q("id")}", 10)).SelectMany(b => b.Records).ToList();
        rows[0][rows[0].Names.First()].ShouldBe("Nouveau");
        D(rows[0][rows[0].Names.ElementAt(1)]).ShouldBe(12.34m);
        rows[1][rows[1].Names.First()].ShouldBe("Accentué é ü 日本");
    }

    protected async Task Upsert_updates_existing_rows_and_inserts_new_ones()
    {
        var result = await Write(Table,
        [
            Record.From(("id", 1L), ("nom", "Modifié"), ("montant", 99.99m)),
            Record.From(("id", 200L), ("nom", "Créé"), ("montant", 1m)),
        ], "upsert", "id");
        result.Succeeded.ShouldBe(2);
        (await Count(Table)).ShouldBe(26);
        var rows = (await ReadAll($"SELECT {Q("id")}, {Q("nom")}, {Q("montant")} FROM {Q(Table)} WHERE {Q("id")} IN (1, 200) ORDER BY {Q("id")}", 10)).SelectMany(b => b.Records).ToList();
        rows.Select(r => r[r.Names.ElementAt(1)]).ShouldBe(["Modifié", "Créé"]);
        D(rows[0][rows[0].Names.ElementAt(2)]).ShouldBe(99.99m);
    }

    protected async Task Upsert_without_key_is_refused()
    {
        await Should.ThrowAsync<ConnectorException>(() => Write(Table, [Record.From(("id", 1L), ("nom", "x"))], "upsert"));
    }
}

public sealed class PostgreSqlConnectorTests() : SqlConformanceCases(new PostgreSqlDialect(), "ERPCONNECT_TEST_POSTGRESQL")
{
    private const string V = "ERPCONNECT_TEST_POSTGRESQL";

    [DatabaseFact(V)] public Task Connection_test() => Connection_test_reports_success_then_a_wrong_password_as_permanent();

    [DatabaseFact(V)] public Task Unreachable() => Unreachable_server_is_a_transient_error();

    [DatabaseFact(V)] public Task Discovery() => Discovery_lists_the_table_and_its_columns();

    [DatabaseFact(V)] public Task Table_read_and_resume() => Reads_a_table_by_batches_with_typed_values_and_exact_resume();

    [DatabaseFact(V)] public Task Query_read_and_resume() => Reads_a_select_query_and_resumes_after_the_rows_already_read();

    [DatabaseFact(V)] public Task Read_only() => Reading_can_never_modify_the_database();

    [DatabaseFact(V)] public Task Escaping() => Table_names_are_escaped_and_never_executed();

    [DatabaseFact(V)] public Task Key_column() => A_table_without_primary_key_needs_a_key_column();

    [DatabaseFact(V)] public Task Insert_rejects() => Insert_rejects_only_the_faulty_rows();

    [DatabaseFact(V)] public Task Upsert() => Upsert_updates_existing_rows_and_inserts_new_ones();

    [DatabaseFact(V)] public Task Upsert_needs_key() => Upsert_without_key_is_refused();

    protected override IEnumerable<string> WritingSelects =>
    [
        .. base.WritingSelects,
        $"WITH d AS (DELETE FROM {Q(Table)} RETURNING *) SELECT * FROM d",
        $"SELECT * INTO {Q(Table + "_copie")} FROM {Q(Table)}",
    ];
}

public sealed class SqlServerConnectorTests() : SqlConformanceCases(new SqlServerDialect(), "ERPCONNECT_TEST_SQLSERVER")
{
    private const string V = "ERPCONNECT_TEST_SQLSERVER";

    protected override string TextType => "NVARCHAR(100)";

    protected override string BoolType => "BIT";

    protected override string Bool(bool b) => b ? "1" : "0";

    protected override string Date(string iso) => $"'{iso}'";

    protected override IEnumerable<string> WritingSelects =>
    [
        .. base.WritingSelects,
        $"WITH d AS (SELECT * FROM {Q(Table)}) DELETE FROM d",
        $"SELECT * INTO {Q(Table + "_copie")} FROM {Q(Table)}",
    ];

    public override async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(V) is not null)
        {
            // La base de test est créée au premier passage (serveur neuf de la CI).
            var db = Settings().Get("database")!;
            await using var c = await Connector.OpenAsync(Settings(v => v["database"] = "master"), CancellationToken.None);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"IF DB_ID(N'{db.Replace("'", "''", StringComparison.Ordinal)}') IS NULL CREATE DATABASE {Q(db)}";
            await cmd.ExecuteNonQueryAsync();
        }

        await base.InitializeAsync();
    }

    [DatabaseFact(V)] public Task Connection_test() => Connection_test_reports_success_then_a_wrong_password_as_permanent();

    [DatabaseFact(V)] public Task Unreachable() => Unreachable_server_is_a_transient_error();

    [DatabaseFact(V)] public Task Discovery() => Discovery_lists_the_table_and_its_columns();

    [DatabaseFact(V)] public Task Table_read_and_resume() => Reads_a_table_by_batches_with_typed_values_and_exact_resume();

    [DatabaseFact(V)] public Task Query_read_and_resume() => Reads_a_select_query_and_resumes_after_the_rows_already_read();

    [DatabaseFact(V)] public Task Read_only() => Reading_can_never_modify_the_database();

    [DatabaseFact(V)] public Task Escaping() => Table_names_are_escaped_and_never_executed();

    [DatabaseFact(V)] public Task Key_column() => A_table_without_primary_key_needs_a_key_column();

    [DatabaseFact(V)] public Task Insert_rejects() => Insert_rejects_only_the_faulty_rows();

    [DatabaseFact(V)] public Task Upsert() => Upsert_updates_existing_rows_and_inserts_new_ones();

    [DatabaseFact(V)] public Task Upsert_needs_key() => Upsert_without_key_is_refused();
}

public sealed class MySqlConnectorTests() : SqlConformanceCases(new MySqlDialect(), "ERPCONNECT_TEST_MYSQL")
{
    private const string V = "ERPCONNECT_TEST_MYSQL";

    protected override IEnumerable<string> WritingSelects =>
    [
        .. base.WritingSelects,
        $"SELECT * FROM {Q(Table)} INTO OUTFILE '/tmp/{Table}.txt'",
    ];

    [DatabaseFact(V)] public Task Connection_test() => Connection_test_reports_success_then_a_wrong_password_as_permanent();

    [DatabaseFact(V)] public Task Unreachable() => Unreachable_server_is_a_transient_error();

    [DatabaseFact(V)] public Task Discovery() => Discovery_lists_the_table_and_its_columns();

    [DatabaseFact(V)] public Task Table_read_and_resume() => Reads_a_table_by_batches_with_typed_values_and_exact_resume();

    [DatabaseFact(V)] public Task Query_read_and_resume() => Reads_a_select_query_and_resumes_after_the_rows_already_read();

    [DatabaseFact(V)] public Task Read_only() => Reading_can_never_modify_the_database();

    [DatabaseFact(V)] public Task Escaping() => Table_names_are_escaped_and_never_executed();

    [DatabaseFact(V)] public Task Key_column() => A_table_without_primary_key_needs_a_key_column();

    [DatabaseFact(V)] public Task Insert_rejects() => Insert_rejects_only_the_faulty_rows();

    [DatabaseFact(V)] public Task Upsert() => Upsert_updates_existing_rows_and_inserts_new_ones();

    [DatabaseFact(V)] public Task Upsert_needs_key() => Upsert_without_key_is_refused();
}

public sealed class OracleConnectorTests() : SqlConformanceCases(new OracleDialect(), "ERPCONNECT_TEST_ORACLE")
{
    private const string V = "ERPCONNECT_TEST_ORACLE";

    protected override string IntType => "NUMBER(10)";

    protected override string TextType => "VARCHAR2(100 CHAR)";

    protected override string DecimalType => "NUMBER(12,2)";

    protected override string BoolType => "NUMBER(1)";

    protected override string Bool(bool b) => b ? "1" : "0";

    protected override IEnumerable<string> WritingSelects =>
    [
        .. base.WritingSelects,
        $"WITH x AS (SELECT 1 AS a FROM dual) SELECT * FROM x FOR UPDATE",
    ];

    /// <summary>
    /// Une transaction Oracle en lecture seule refuse, pendant quelques secondes, une table qui vient d'être créée
    /// (ORA-01466, classé transitoire). En production les tables lues ne sont pas créées à l'instant ; le test attend.
    /// </summary>
    protected override async Task AfterDdl(string table)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                await ReadAll($"SELECT COUNT(*) AS n FROM {Q(table)}", 1);
                return;
            }
            catch (ConnectorException ex) when (ex.Kind == ErrorKind.Transient && ex.Message.Contains("ORA-01466", StringComparison.Ordinal) && i < 40)
            {
                await Task.Delay(500);
            }
        }
    }

    [DatabaseFact(V)] public Task Connection_test() => Connection_test_reports_success_then_a_wrong_password_as_permanent();

    [DatabaseFact(V)] public Task Unreachable() => Unreachable_server_is_a_transient_error();

    [DatabaseFact(V)] public Task Discovery() => Discovery_lists_the_table_and_its_columns();

    [DatabaseFact(V)] public Task Table_read_and_resume() => Reads_a_table_by_batches_with_typed_values_and_exact_resume();

    [DatabaseFact(V)] public Task Query_read_and_resume() => Reads_a_select_query_and_resumes_after_the_rows_already_read();

    [DatabaseFact(V)] public Task Read_only() => Reading_can_never_modify_the_database();

    [DatabaseFact(V)] public Task Escaping() => Table_names_are_escaped_and_never_executed();

    [DatabaseFact(V)] public Task Key_column() => A_table_without_primary_key_needs_a_key_column();

    [DatabaseFact(V)] public Task Insert_rejects() => Insert_rejects_only_the_faulty_rows();

    [DatabaseFact(V)] public Task Upsert() => Upsert_updates_existing_rows_and_inserts_new_ones();

    [DatabaseFact(V)] public Task Upsert_needs_key() => Upsert_without_key_is_refused();
}

/// <summary>ODBC testé avec le pilote psqlODBC vers le serveur PostgreSQL de test.</summary>
public sealed class OdbcConnectorTests() : SqlConformanceCases(new OdbcDialect(), "ERPCONNECT_TEST_ODBC")
{
    private const string V = "ERPCONNECT_TEST_ODBC";

    [DatabaseFact(V)] public Task Connection_test() => Connection_test_reports_success_then_a_wrong_password_as_permanent();

    // Les pilotes ODBC renvoient le même code (08001) pour un serveur injoignable et un mot de passe faux : l'échec reste permanent.
    [DatabaseFact(V)] public Task Unreachable_is_reported_but_permanent() => Unreachable_server_is_a_transient_error(ErrorKind.Permanent);

    [DatabaseFact(V)] public Task Discovery() => Discovery_lists_the_table_and_its_columns();

    [DatabaseFact(V)] public Task Table_read_and_resume_with_key_option()
    {
        // ODBC ne donne pas la clé primaire de façon portable : la colonne de tri est indiquée.
        return Run(async () =>
        {
            var batches = await ReadAll(Table, 10, options: new() { ["key"] = "id" });
            batches.Select(b => b.Records.Count).ShouldBe([10, 10, 5]);
            var rest = await ReadAll(Table, 10, batches[0].Cursor, new() { ["key"] = "id" });
            rest.SelectMany(b => b.Records).Select(r => N(r["id"])).ShouldBe(Enumerable.Range(11, 15).Select(i => (long)i));
        });
    }

    [DatabaseFact(V)] public Task Query_read_and_resume() => Reads_a_select_query_and_resumes_after_the_rows_already_read();

    [DatabaseFact(V)] public Task Read_only() => Reading_can_never_modify_the_database();

    [DatabaseFact(V)] public Task Escaping() => Table_names_are_escaped_and_never_executed();

    [DatabaseFact(V)] public Task Insert_rejects() => Insert_rejects_only_the_faulty_rows();

    [DatabaseFact(V)] public Task Upsert_is_refused_with_a_clear_message()
    {
        return Run(async () =>
        {
            var ex = await Should.ThrowAsync<ConnectorException>(() => Write(Table, [Record.From(("id", 1L), ("nom", "x"))], "upsert", "id"));
            ex.Message.ShouldContain("insertion");
        });
    }

    [Fact]
    public void A_password_inside_the_connection_string_is_refused()
    {
        var s = new ConnectorSettings(new Dictionary<string, string?> { ["connectionString"] = "Driver={X};Server=h;PWD=secret" });
        Should.Throw<ConnectorException>(() => Dialect.CreateConnection(s)).Message.ShouldContain("Mot de passe");
    }
}

/// <summary>Tests sans serveur : échappement, requêtes générées, schémas de configuration.</summary>
public sealed class SqlDialectTests
{
    public static TheoryData<SqlDialect> All => new() { new PostgreSqlDialect(), new SqlServerDialect(), new MySqlDialect(), new OracleDialect(), new OdbcDialect() };

    [Theory]
    [MemberData(nameof(All))]
    public void Identifiers_are_escaped_and_invalid_ones_refused(SqlDialect d)
    {
        d.QuoteName("a\"b`c]d").ShouldNotBeNullOrEmpty();
        d.QuoteQualified("dbo.clients").Split('.').Length.ShouldBe(2);
        Should.Throw<ConnectorException>(() => d.QuoteName(""));
        Should.Throw<ConnectorException>(() => d.QuoteName("a\0b"));
        Should.Throw<ConnectorException>(() => d.QuoteName(new string('x', 129)));
        Should.Throw<ConnectorException>(() => d.QuoteQualified("a.b.c.d"));
    }

    [Fact]
    public void Each_engine_escapes_its_own_quote_character()
    {
        new PostgreSqlDialect().QuoteName("a\"b").ShouldBe("\"a\"\"b\"");
        new SqlServerDialect().QuoteName("a]b").ShouldBe("[a]]b]");
        new MySqlDialect().QuoteName("a`b").ShouldBe("`a``b`");
    }

    [Theory]
    [MemberData(nameof(All))]
    public void Passwords_are_secret_fields(SqlDialect d)
    {
        var c = new SqlConnector(d);
        c.Manifest.ConfigSchema.Single(f => f.Name == "password").Secret.ShouldBeTrue();
        c.Manifest.ConfigSchema.Where(f => f.Name != "password").ShouldAllBe(f => !f.Secret);
    }

    [Fact]
    public void Tls_is_required_and_verified_by_default()
    {
        foreach (var d in new SqlDialect[] { new PostgreSqlDialect(), new SqlServerDialect(), new MySqlDialect(), new OracleDialect() })
        {
            var f = d.Fields.ToDictionary(x => x.Name);
            f["tls"].Default.ShouldBe("require");
            f["trustServerCertificate"].Default.ShouldBe("false");
        }

        var s = new ConnectorSettings(
            new Dictionary<string, string?> { ["host"] = "h", ["database"] = "b", ["username"] = "u" },
            new Dictionary<string, string> { ["password"] = "p" });
        new PostgreSqlDialect().CreateConnection(s).ConnectionString.ShouldContain("SSL Mode=VerifyFull");
        var sql = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(new SqlServerDialect().CreateConnection(s).ConnectionString);
        sql.Encrypt.ShouldBe(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory);
        sql.TrustServerCertificate.ShouldBeFalse();
        new MySqlDialect().CreateConnection(s).ConnectionString.ShouldContain("SSL Mode=VerifyFull");
        new OracleDialect().CreateConnection(s).ConnectionString.ShouldContain("PROTOCOL=TCPS");
    }

    [Fact]
    public void Oracle_refuses_descriptor_injection_in_host_or_service()
    {
        var s = new ConnectorSettings(
            new Dictionary<string, string?> { ["host"] = "h)(HOST=autre", ["database"] = "b", ["username"] = "u" },
            new Dictionary<string, string> { ["password"] = "p" });
        Should.Throw<ConnectorException>(() => new OracleDialect().CreateConnection(s));
    }

    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("  with x as (select 1) select * from x", true)]
    [InlineData("DELETE FROM t", false)]
    [InlineData("clients", false)]
    [InlineData("dbo.clients", false)]
    public void Queries_are_told_apart_from_table_names(string resource, bool query) => SqlConnector.IsQuery(resource).ShouldBe(query);

    [Fact]
    public void Only_one_statement_is_allowed() => Should.Throw<ConnectorException>(() => SqlConnector.CheckQuery("SELECT 1; SELECT 2"));

    [Fact]
    public void Upsert_statements_use_the_key_and_update_the_other_columns()
    {
        var cols = new[] { "[id]", "[nom]" };
        new SqlServerDialect().Upsert("[t]", cols, ["[id]"]).ShouldContain("WHEN MATCHED THEN UPDATE SET d.[nom] = s.[nom]");
        new PostgreSqlDialect().Upsert("\"t\"", ["\"id\"", "\"nom\""], ["\"id\""]).ShouldEndWith("ON CONFLICT (\"id\") DO UPDATE SET \"nom\" = EXCLUDED.\"nom\"");
        new PostgreSqlDialect().Upsert("\"t\"", ["\"id\""], ["\"id\""]).ShouldEndWith("DO NOTHING");
        new OracleDialect().Upsert("\"T\"", ["\"ID\""], ["\"ID\""]).ShouldNotContain("WHEN MATCHED");
    }

    [Fact]
    public void All_engines_are_registered()
    {
        ConnectorRegistry.CreateDefault().Select(c => c.Manifest.Id).ShouldContain("db.sqlserver");
        ConnectorRegistry.CreateDefault().Select(c => c.Manifest.Id).Where(i => i.StartsWith("db.", StringComparison.Ordinal))
            .ShouldBe(["db.sqlite", "db.postgresql", "db.sqlserver", "db.mysql", "db.oracle", "db.odbc"]);
    }
}
