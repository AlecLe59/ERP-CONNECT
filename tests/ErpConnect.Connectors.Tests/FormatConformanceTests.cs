using ErpConnect.Connectors.Formats;
using ErpConnect.Connectors.Sdk;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

/// <summary>
/// Suite de conformance commune à tout format : aller-retour sans perte, entrée vide, entrée invalide classée
/// permanente, annulation respectée, gros volume lu en flux.
/// </summary>
public abstract class FormatConformanceTests
{
    protected abstract IRecordFormat Format { get; }

    protected virtual Dictionary<string, string> Options => [];

    /// <summary>Contenu invalide pour ce format.</summary>
    protected abstract byte[] Malformed { get; }

    protected virtual string Normalize(object? v) => Record.ToInvariantString(v) ?? string.Empty;

    [Fact]
    public async Task Round_trip_preserves_values_and_order()
    {
        var input = Samples.Tricky();
        var bytes = await Samples.WriteAll(Format, input, Options);
        var output = await Samples.ReadAll(Format, new MemoryStream(bytes), Options);
        output.Count.ShouldBe(input.Count);
        for (var i = 0; i < input.Count; i++)
        {
            output[i].Names.ShouldBe(input[i].Names);
            foreach (var n in input[i].Names)
            {
                Normalize(output[i][n]).ShouldBe(Normalize(input[i][n]), $"{i}.{n}");
            }
        }
    }

    [Fact]
    public async Task Empty_document_yields_no_record()
    {
        var bytes = await Samples.WriteAll(Format, [], Options);
        (await Samples.ReadAll(Format, new MemoryStream(bytes), Options)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Malformed_input_is_a_permanent_connector_error()
    {
        var ex = await Should.ThrowAsync<ConnectorException>(() => Samples.ReadAll(Format, new MemoryStream(Malformed), Options));
        ex.Kind.ShouldBe(ErrorKind.Permanent);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var bytes = await Samples.WriteAll(Format, Enumerable.Range(0, 5000).Select(i => Record.From(("n", (long)i), ("t", "x"))), Options);
        using var cts = new CancellationTokenSource();
        var count = 0;
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Format.ReadAsync(new MemoryStream(bytes), Options, cts.Token))
            {
                if (++count == 10)
                {
                    await cts.CancelAsync();
                }
            }
        });
        count.ShouldBeLessThan(5000);
    }

    [Fact]
    public async Task Large_volume_is_streamed()
    {
        const int n = 200_000;
        var path = Path.GetTempFileName();
        try
        {
            await using (var fs = File.Create(path))
            await using (var w = Format.CreateWriter(fs, Options))
            {
                for (var i = 0; i < n; i++)
                {
                    await w.WriteAsync(Record.From(("id", (long)i), ("libelle", $"Article {i}"), ("prix", 12.5m)), CancellationToken.None);
                }

                await w.FinishAsync(CancellationToken.None);
            }

            GC.Collect();
            var before = GC.GetTotalMemory(true);
            long peak = 0;
            var count = 0;
            await using (var fs = File.OpenRead(path))
            {
                await foreach (var r in Format.ReadAsync(fs, Options, CancellationToken.None))
                {
                    count++;
                    if (count % 50_000 == 0)
                    {
                        peak = Math.Max(peak, GC.GetTotalMemory(true) - before);
                    }
                }
            }

            count.ShouldBe(n);
            peak.ShouldBeLessThan(MaxStreamingHeapBytes, "la lecture doit être en flux");
        }
        finally
        {
            File.Delete(path);
        }
    }

    protected virtual long MaxStreamingHeapBytes => 64L * 1024 * 1024;
}

public sealed class CsvConformance : FormatConformanceTests
{
    protected override IRecordFormat Format { get; } = new CsvFormat();

    protected override byte[] Malformed => "a;b\n\"non ferme;2\n"u8.ToArray();
}

public sealed class CsvCommaConformance : FormatConformanceTests
{
    protected override IRecordFormat Format { get; } = new CsvFormat();

    protected override Dictionary<string, string> Options => new() { ["delimiter"] = ",", ["encoding"] = "windows-1252" };

    protected override byte[] Malformed => "a,b\n1,2,3\n"u8.ToArray();

    // windows-1252 ne sait pas coder le japonais : on compare après remplacement par « ? ».
    protected override string Normalize(object? v) => (Record.ToInvariantString(v) ?? string.Empty).Replace("日本", "??", StringComparison.Ordinal);
}

public sealed class JsonConformance : FormatConformanceTests
{
    protected override IRecordFormat Format { get; } = new JsonFormat();

    protected override byte[] Malformed => "[{\"a\":1},{\"a\":"u8.ToArray();
}

public sealed class JsonLinesConformance : FormatConformanceTests
{
    protected override IRecordFormat Format { get; } = new JsonFormat();

    protected override Dictionary<string, string> Options => new() { ["layout"] = "lines" };

    protected override byte[] Malformed => "{\"a\":1}\n{oops}\n"u8.ToArray();
}

public sealed class XmlConformance : FormatConformanceTests
{
    protected override IRecordFormat Format { get; } = new XmlFormat();

    protected override Dictionary<string, string> Options => new() { ["recordElement"] = "Client", ["rootElement"] = "Clients" };

    protected override byte[] Malformed => "<Clients><Client><code>1</code></Clients>"u8.ToArray();

    // XML : un élément vide se relit comme chaîne vide ou null, les deux sont équivalents ici.
    protected override string Normalize(object? v) => Record.ToInvariantString(v) ?? string.Empty;
}

public sealed class XlsxConformance : FormatConformanceTests
{
    protected override IRecordFormat Format { get; } = new XlsxFormat();

    protected override byte[] Malformed => "PK\u0003\u0004 pas une archive"u8.ToArray();

    // MiniExcel construit la table des chaînes partagées : plus de mémoire qu'un format texte.
    protected override long MaxStreamingHeapBytes => 200L * 1024 * 1024;
}
