using System.IO.Compression;
using System.Text;
using ErpConnect.Connectors.Formats;
using ErpConnect.Connectors.Sdk;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

/// <summary>Fichiers malveillants : chaque format refuse proprement, sans consommer de ressources démesurées.</summary>
public class FormatSecurityTests
{
    [Fact]
    public async Task Xml_external_entity_is_refused()
    {
        var secret = Path.GetTempFileName();
        await File.WriteAllTextAsync(secret, "SECRET-XXE");
        var xml = $"<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file://{secret}\">]><Records><Record><a>&x;</a></Record></Records>";
        var ex = await Should.ThrowAsync<ConnectorException>(() => Samples.ReadAll(new XmlFormat(), Samples.Utf8(xml)));
        ex.Message.ShouldNotContain("SECRET-XXE");
        File.Delete(secret);
    }

    [Fact]
    public async Task Xml_billion_laughs_is_refused()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">]><Records><Record><a>&lol2;</a></Record></Records>";
        await Should.ThrowAsync<ConnectorException>(() => Samples.ReadAll(new XmlFormat(), Samples.Utf8(xml)));
    }

    [Fact]
    public async Task Xml_attributes_nesting_and_repetition_are_mapped()
    {
        var xml = "<Records><Record id=\"7\"><client><nom>A</nom></client><ligne>1</ligne><ligne>2</ligne></Record></Records>";
        var r = (await Samples.ReadAll(new XmlFormat(), Samples.Utf8(xml))).Single();
        r["@id"].ShouldBe("7");
        r.GetPath("client.nom").ShouldBe("A");
        ((IReadOnlyList<object?>)r["ligne"]!).ShouldBe(["1", "2"]);
    }

    [Fact]
    public async Task Json_excessive_depth_is_refused()
    {
        var json = "[" + new string('[', 200) + new string(']', 200) + "]";
        await Should.ThrowAsync<ConnectorException>(() => Samples.ReadAll(new JsonFormat(), Samples.Utf8(json)));
    }

    [Fact]
    public async Task Json_non_object_item_is_refused()
    {
        var ex = await Should.ThrowAsync<ConnectorException>(() => Samples.ReadAll(new JsonFormat(), Samples.Utf8("[1,2]")));
        ex.Message.ShouldContain("objet JSON attendu");
    }

    [Fact]
    public async Task Json_types_are_preserved()
    {
        var r = (await Samples.ReadAll(new JsonFormat(), Samples.Utf8("[{\"i\":42,\"d\":1.25,\"b\":true,\"n\":null,\"o\":{\"x\":\"y\"},\"a\":[1,2]}]"))).Single();
        r["i"].ShouldBe(42L);
        r["d"].ShouldBe(1.25m);
        r["b"].ShouldBe(true);
        r["n"].ShouldBeNull();
        r.GetPath("o.x").ShouldBe("y");
        r.GetPath("a.1").ShouldBe(2L);
    }

    [Fact]
    public async Task Csv_oversized_field_is_refused()
    {
        var csv = "a\n" + new string('x', CsvFormat.MaxFieldLength + 10) + "\n";
        await Should.ThrowAsync<ConnectorException>(() => Samples.ReadAll(new CsvFormat(), Samples.Utf8(csv)));
    }

    [Fact]
    public async Task Csv_windows_1252_is_decoded()
    {
        var bytes = Encoding.GetEncoding(1252).GetBytes("nom\nÉlodie\n");
        var r = (await Samples.ReadAll(new CsvFormat(), new MemoryStream(bytes), new() { ["encoding"] = "windows-1252" })).Single();
        r["nom"].ShouldBe("Élodie");
    }

    [Fact]
    public async Task Csv_duplicate_and_empty_headers_are_made_unique()
    {
        var r = (await Samples.ReadAll(new CsvFormat(), Samples.Utf8("a;a;\n1;2;3\n"))).Single();
        r.Names.ShouldBe(["a", "a_2", "col3"]);
    }

    [Fact]
    public async Task Csv_refuses_nested_values_on_write()
    {
        await Should.ThrowAsync<ConnectorException>(() => Samples.WriteAll(new CsvFormat(), [Record.From(("a", Record.From(("b", 1L))))]));
    }

    [Fact]
    public async Task Xlsx_zip_bomb_is_refused()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize);
            await using var s = e.Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 50; i++)
            {
                await s.WriteAsync(zeros);
            }
        }

        ms.Position = 0;
        var ex = await Should.ThrowAsync<ConnectorException>(() => Samples.ReadAll(new XlsxFormat(), ms));
        ex.Message.ShouldContain("compression");
    }

    [Fact]
    public async Task Xlsx_types_survive_round_trip()
    {
        var bytes = await Samples.WriteAll(new XlsxFormat(), [Record.From(("n", 3L), ("p", 2.5m), ("t", "x"))]);
        var r = (await Samples.ReadAll(new XlsxFormat(), new MemoryStream(bytes))).Single();
        r["n"].ShouldBe(3L);
        r["p"].ShouldBe(2.5m);
        r["t"].ShouldBe("x");
    }

    [Fact]
    public void Record_refuses_unknown_types_and_excessive_depth()
    {
        Should.Throw<ConnectorException>(() => Record.From(("x", new object())));
        var deep = new Record();
        var cur = deep;
        for (var i = 0; i < Record.MaxDepth + 2; i++)
        {
            var next = new Record();
            cur.Set("c", next);
            cur = next;
        }

        Should.Throw<ConnectorException>(() => new Record().Set("deep", deep));
    }
}
