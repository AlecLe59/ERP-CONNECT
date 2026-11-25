using ErpConnect.Connectors.Sdk;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

public class FileConnectorTests
{
    private static ConnectorSettings Local(string root) => new(new Dictionary<string, string?> { ["rootPath"] = root });

    private static async Task WriteCsv(string path, int rows, int offset = 0)
    {
        await using var w = new StreamWriter(path);
        await w.WriteLineAsync("id;nom");
        for (var i = 0; i < rows; i++)
        {
            await w.WriteLineAsync($"{i + offset};nom {i + offset}");
        }
    }

    private static async Task<List<RecordBatch>> ReadAll(IConnector c, ConnectorSettings s, ReadRequest req, string? cursor = null)
    {
        var list = new List<RecordBatch>();
        await using var reader = c.CreateReader(s, req);
        await foreach (var b in reader.ReadAsync(cursor, CancellationToken.None))
        {
            list.Add(b);
        }

        return list;
    }

    [Fact]
    public async Task Reads_several_files_in_name_order_by_batches()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("in"));
        await WriteCsv(dir.File("in/b.csv"), 5, 100);
        await WriteCsv(dir.File("in/a.csv"), 7);
        var batches = await ReadAll(new LocalFolderConnector(), Local(dir.Path), new ReadRequest("in/*.csv", 3, new Dictionary<string, string> { ["format"] = "csv" }));
        var ids = batches.SelectMany(b => b.Records).Select(r => (string)r["id"]!).ToList();
        ids.ShouldBe([.. Enumerable.Range(0, 7).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), .. Enumerable.Range(100, 5).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        batches.ShouldAllBe(b => b.Records.Count <= 3);
    }

    [Fact]
    public async Task Resuming_from_any_cursor_gives_exactly_the_remaining_records()
    {
        using var dir = new TempDir();
        await WriteCsv(dir.File("a.csv"), 10);
        await WriteCsv(dir.File("b.csv"), 4, 50);
        var req = new ReadRequest("*.csv", 3, new Dictionary<string, string> { ["format"] = "csv" });
        var connector = new LocalFolderConnector();
        var all = await ReadAll(connector, Local(dir.Path), req);
        var expected = all.SelectMany(b => b.Records).Select(r => (string)r["id"]!).ToList();
        for (var k = 0; k < all.Count; k++)
        {
            var before = all.Take(k + 1).SelectMany(b => b.Records).Select(r => (string)r["id"]!);
            var after = (await ReadAll(connector, Local(dir.Path), req, all[k].Cursor)).SelectMany(b => b.Records).Select(r => (string)r["id"]!);
            before.Concat(after).ShouldBe(expected, $"reprise après le lot {k}");
        }
    }

    [Fact]
    public async Task Resume_refuses_when_source_files_changed()
    {
        using var dir = new TempDir();
        await WriteCsv(dir.File("a.csv"), 5);
        await WriteCsv(dir.File("b.csv"), 5);
        var req = new ReadRequest("*.csv", 2, new Dictionary<string, string> { ["format"] = "csv" });
        var first = (await ReadAll(new LocalFolderConnector(), Local(dir.Path), req)).First(b => b.Cursor!.StartsWith("1:", StringComparison.Ordinal));
        File.Move(dir.File("b.csv"), dir.File("c.csv"));
        await Should.ThrowAsync<ConnectorException>(() => ReadAll(new LocalFolderConnector(), Local(dir.Path), req, first.Cursor));
    }

    [Fact]
    public async Task Writer_publishes_only_on_complete()
    {
        using var dir = new TempDir();
        var c = new LocalFolderConnector();
        var req = new WriteRequest("out/{runId}.json", WriteMode.CreateNew, new Dictionary<string, string> { ["format"] = "json" }, "R42");
        await using (var w = c.CreateWriter(Local(dir.Path), req))
        {
            (await w.WriteBatchAsync(Samples.Tricky(), CancellationToken.None)).Succeeded.ShouldBe(4);
            File.Exists(dir.File("out/R42.json")).ShouldBeFalse();
            await w.CompleteAsync(CancellationToken.None);
        }

        File.Exists(dir.File("out/R42.json")).ShouldBeTrue();
        Directory.GetFiles(dir.File("out")).Length.ShouldBe(1);
    }

    [Fact]
    public async Task Writer_disposed_without_complete_publishes_nothing()
    {
        using var dir = new TempDir();
        var c = new LocalFolderConnector();
        await using (var w = c.CreateWriter(Local(dir.Path), new WriteRequest("out.csv", WriteMode.CreateNew, new Dictionary<string, string> { ["format"] = "csv" })))
        {
            await w.WriteBatchAsync(Samples.Tricky(), CancellationToken.None);
        }

        Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Writer_surfaces_format_errors()
    {
        using var dir = new TempDir();
        await using var w = new LocalFolderConnector().CreateWriter(Local(dir.Path), new WriteRequest("x.csv", WriteMode.CreateNew, new Dictionary<string, string> { ["format"] = "csv" }));
        await Should.ThrowAsync<ConnectorException>(() => w.WriteBatchAsync([Record.From(("a", 1L)), Record.From(("b", 2L))], CancellationToken.None));
    }

    [Fact]
    public async Task Test_and_discover_and_sample()
    {
        using var dir = new TempDir();
        await WriteCsv(dir.File("clients.csv"), 3);
        var c = new LocalFolderConnector();
        (await c.TestAsync(Local(dir.Path), CancellationToken.None)).Success.ShouldBeTrue();
        (await c.TestAsync(Local(dir.File("absent")), CancellationToken.None)).Message.ShouldContain("0 fichier");
        (await c.DiscoverAsync(Local(dir.Path), "*.csv", CancellationToken.None)).Resources.ShouldBe(["clients.csv"]);
        var sample = await c.SampleAsync(Local(dir.Path), new ReadRequest("clients.csv", Options: new Dictionary<string, string> { ["format"] = "csv" }), 10, CancellationToken.None);
        sample.Fields.Select(f => f.Name).ShouldBe(["id", "nom"]);
    }

    [Fact]
    public void Settings_are_validated_against_the_manifest()
    {
        var c = new SftpConnector();
        var errors = new ConnectorSettings(new Dictionary<string, string?> { ["host"] = "h", ["port"] = "abc", ["inconnu"] = "x" }).ValidateAgainst(c.Manifest.ConfigSchema);
        errors.Select(e => e.Field).ShouldBe(["port", "username", "hostKeyFingerprint", "inconnu"], ignoreOrder: true);
    }

    [Fact]
    public void Sftp_requires_a_host_key_fingerprint()
    {
        Should.Throw<ConnectorException>(() => new Transports.SftpTransport(new("h", 22, "u", "p", null, null, "", "/", TimeSpan.FromSeconds(1))));
        Transports.SftpTransport.FingerprintMatches("SHA256:abcd", "abcd=").ShouldBeTrue();
        Transports.SftpTransport.FingerprintMatches("SHA256:abcd", "abce").ShouldBeFalse();
        Transports.SftpTransport.FingerprintMatches("", "").ShouldBeFalse();
    }
}

[Trait("Category", "Sftp")]
public class SftpConnectorTests : IClassFixture<SftpServerFixture>
{
    private readonly SftpServerFixture _server;

    public SftpConnectorTests(SftpServerFixture server) => _server = server;

    private ConnectorSettings Settings(string root, string? fingerprint = null) => new(
        new Dictionary<string, string?>
        {
            ["host"] = "127.0.0.1", ["port"] = _server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["username"] = _server.User,
            ["hostKeyFingerprint"] = fingerprint ?? _server.Fingerprint, ["rootPath"] = root,
        },
        new Dictionary<string, string> { ["privateKey"] = _server.ClientKey });

    [Fact]
    public async Task Wrong_host_key_is_refused()
    {
        var r = await new SftpConnector().TestAsync(Settings(_server.DataPath, "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), CancellationToken.None);
        r.Success.ShouldBeFalse();
        r.Message.ShouldContain("Clé d'hôte inattendue");
    }

    [Fact]
    public async Task Connection_test_succeeds_with_the_right_key()
    {
        (await new SftpConnector().TestAsync(Settings(_server.DataPath), CancellationToken.None)).Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Csv_round_trip_through_sftp()
    {
        var root = _server.DataPath + "/rt-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        var c = new SftpConnector();
        await using (var w = c.CreateWriter(Settings(root), new WriteRequest("out/data.csv", WriteMode.CreateNew, new Dictionary<string, string> { ["format"] = "csv" })))
        {
            await w.WriteBatchAsync(Samples.Tricky(), CancellationToken.None);
            await w.CompleteAsync(CancellationToken.None);
        }

        var read = new List<Record>();
        await using var reader = c.CreateReader(Settings(root), new ReadRequest("out/*.csv", 2, new Dictionary<string, string> { ["format"] = "csv" }));
        await foreach (var b in reader.ReadAsync(null, CancellationToken.None))
        {
            read.AddRange(b.Records);
        }

        read.Select(r => r["nom"]).ShouldBe(Samples.Tricky().Select(r => r["nom"]));
    }
}
