using System.IO.Compression;
using System.Text;
using ErpConnect.Connectors.Sdk;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

public class ArchiveTests
{
    private static ConnectorSettings Local(string root) => new(new Dictionary<string, string?> { ["rootPath"] = root });

    private static Dictionary<string, string> Zip(string format, string? pattern = null, string? entryName = null)
    {
        var d = new Dictionary<string, string> { ["format"] = format, ["archive"] = "zip" };
        if (pattern is not null)
        {
            d["entryPattern"] = pattern;
        }

        if (entryName is not null)
        {
            d["entryName"] = entryName;
        }

        return d;
    }

    private static void MakeZip(string path, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    private static async Task<List<Record>> ReadAll(ReadRequest req, string root, string? cursor = null)
    {
        var list = new List<Record>();
        await using var reader = new LocalFolderConnector().CreateReader(Local(root), req);
        await foreach (var b in reader.ReadAsync(cursor, CancellationToken.None))
        {
            list.AddRange(b.Records);
        }

        return list;
    }

    [Fact]
    public async Task Reads_matching_entries_of_every_archive_in_order()
    {
        using var dir = new TempDir();
        MakeZip(dir.File("a.zip"), ("clients/2.csv", "id\n3\n4\n"), ("clients/1.csv", "id\n1\n2\n"), ("lisez-moi.txt", "ignoré"));
        MakeZip(dir.File("b.zip"), ("x.csv", "id\n5\n"));
        var rows = await ReadAll(new ReadRequest("*.zip", 2, Zip("csv", "*.csv")), dir.Path);
        rows.Select(r => (string)r["id"]!).ShouldBe(["1", "2", "3", "4", "5"]);
    }

    [Fact]
    public async Task Resume_cursor_points_inside_an_archive()
    {
        using var dir = new TempDir();
        MakeZip(dir.File("a.zip"), ("1.csv", "id\n1\n2\n3\n"), ("2.csv", "id\n4\n5\n"));
        var req = new ReadRequest("a.zip", 2, Zip("csv"));
        var batches = new List<RecordBatch>();
        await using (var reader = new LocalFolderConnector().CreateReader(Local(dir.Path), req))
        {
            await foreach (var b in reader.ReadAsync(null, CancellationToken.None))
            {
                batches.Add(b);
            }
        }

        var rest = await ReadAll(req, dir.Path, batches[0].Cursor);
        rest.Select(r => (string)r["id"]!).ShouldBe(["3", "4", "5"]);
    }

    [Fact]
    public async Task Writes_a_zip_with_one_entry_atomically()
    {
        using var dir = new TempDir();
        await using (var w = new LocalFolderConnector().CreateWriter(Local(dir.Path), new WriteRequest("export-{runId}.zip", WriteMode.CreateNew, Zip("csv", entryName: "clients.csv"), "R1")))
        {
            await w.WriteBatchAsync([new Record().Set("id", 1L).Set("nom", "A"), new Record().Set("id", 2L).Set("nom", "B")], default);
            File.Exists(dir.File("export-R1.zip")).ShouldBeFalse("rien n'est publié avant la fin");
            await w.CompleteAsync(default);
        }

        using var zip = ZipFile.OpenRead(dir.File("export-R1.zip"));
        zip.Entries.Select(e => e.FullName).ShouldBe(["clients.csv"]);
        using var text = new StreamReader(zip.Entries[0].Open());
        (await text.ReadToEndAsync()).ShouldContain("2;B");
    }

    [Fact]
    public async Task Zip_slip_entry_names_are_refused()
    {
        using var dir = new TempDir();
        MakeZip(dir.File("evil.zip"), ("../../evil.csv", "id\n1\n"));
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(new ReadRequest("evil.zip", 10, Zip("csv")), dir.Path));
        ex.Kind.ShouldBe(ErrorKind.Permanent);
    }

    [Fact]
    public async Task Writing_with_a_traversal_entry_name_is_refused()
    {
        using var dir = new TempDir();
        await Should.ThrowAsync<ConnectorException>(async () =>
        {
            await using var w = new LocalFolderConnector().CreateWriter(Local(dir.Path), new WriteRequest("x.zip", WriteMode.CreateNew, Zip("csv", entryName: "../x.csv")));
            await w.WriteBatchAsync([new Record().Set("id", 1L)], default);
            await w.CompleteAsync(default);
        });
    }

    [Fact]
    public async Task Compression_bombs_are_refused_before_decompression()
    {
        using var dir = new TempDir();
        using (var zip = ZipFile.Open(dir.File("bomb.zip"), ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("bomb.csv", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 64; i++)
            {
                s.Write(zeros);
            }
        }

        new FileInfo(dir.File("bomb.zip")).Length.ShouldBeLessThan(1024 * 1024);
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(new ReadRequest("bomb.zip", 10, Zip("csv")), dir.Path));
        ex.Message.ShouldContain("taux de compression");
    }

    [Fact]
    public async Task A_corrupted_archive_is_a_permanent_error()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("bad.zip"), "ceci n'est pas une archive");
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAll(new ReadRequest("bad.zip", 10, Zip("csv")), dir.Path));
        ex.Kind.ShouldBe(ErrorKind.Permanent);
    }
}
