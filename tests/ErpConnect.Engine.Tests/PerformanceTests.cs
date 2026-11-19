using System.Diagnostics;
using ErpConnect.Connectors.Tests;
using ErpConnect.Core.Runs;
using ErpConnect.Flows;
using Xunit.Abstractions;

namespace ErpConnect.Engine.Tests;

/// <summary>Critères de la Phase 3 : un million de lignes sous 500 Mo de mémoire (pic de l'ensemble du processus de test).</summary>
[Trait("Category", "Performance")]
public sealed class PerformanceTests
{
    private const int Rows = 1_000_000;
    private const long MaxBytes = 500L * 1024 * 1024;
    private readonly ITestOutputHelper _output;

    public PerformanceTests(ITestOutputHelper output) => _output = output;

    private static async Task<(T Result, long PeakBytes, TimeSpan Elapsed)> MeasureAsync<T>(Func<Task<T>> action)
    {
        using var process = Process.GetCurrentProcess();
        long peak = 0;
        using var cts = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                await Task.Delay(50);
            }
        });
        var sw = Stopwatch.StartNew();
        var result = await action();
        sw.Stop();
        await cts.CancelAsync();
        await sampler;
        return (result, peak, sw.Elapsed);
    }

    [Fact]
    public async Task One_million_csv_rows_to_rest_stay_under_500_MB()
    {
        await using var k = await Kernel.StartAsync();
        var input = k.Folder("in");
        await Kernel.WriteCsvAsync(Path.Combine(input, "big.csv"), Rows);
        long received = 0;
        using var server = new TestHttpServer((_, body) =>
        {
            Interlocked.Add(ref received, body.Count(c => c == '{'));
            return (200, "{}", null);
        }, record: false);
        await k.LocalAsync("src", input);
        await k.RestAsync("api", server.BaseUrl);
        var (run, peak, elapsed) = await MeasureAsync(() => k.FlowRunAsync(Kernel.Definition("src", "*.csv", "csv", "api", "/import", [new("mode", "batch")], batch: 1000)));
        _output.WriteLine($"CSV→REST : {elapsed.TotalSeconds:F1} s, pic {peak / 1024 / 1024} Mo");
        run.ShouldHaveStatus(RunStatus.Succeeded);
        run.WrittenCount.ShouldBe(Rows);
        Interlocked.Read(ref received).ShouldBe(Rows);
        peak.ShouldBeLessThan(MaxBytes);
    }

    [Fact]
    [Trait("Category", "Sftp")]
    public async Task One_million_rows_from_sftp_to_json_stay_under_500_MB()
    {
        using var sftp = new SftpServerFixture();
        await using var k = await Kernel.StartAsync();
        var remote = Path.Combine(sftp.DataPath, "big");
        Directory.CreateDirectory(remote);
        await Kernel.WriteCsvAsync(Path.Combine(remote, "big.csv"), Rows);
        var output = k.Folder("out");
        await k.Connections.SaveAsync(Kernel.Admin, "sftp", "sftp", "file.sftp",
            new Dictionary<string, string?>
            {
                ["host"] = "127.0.0.1", ["port"] = sftp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["username"] = sftp.User,
                ["hostKeyFingerprint"] = sftp.Fingerprint, ["rootPath"] = remote,
            },
            new Dictionary<string, string?> { ["privateKey"] = sftp.ClientKey }, [], 0, "t", default);
        await k.LocalAsync("dst", output);
        var (run, peak, elapsed) = await MeasureAsync(() => k.FlowRunAsync(Kernel.Definition("sftp", "*.csv", "csv", "dst", "big.json", batch: 1000)));
        _output.WriteLine($"SFTP→JSON : {elapsed.TotalSeconds:F1} s, pic {peak / 1024 / 1024} Mo");
        run.ShouldHaveStatus(RunStatus.Succeeded);
        run.WrittenCount.ShouldBe(Rows);
        new FileInfo(Path.Combine(output, "big.json")).Length.ShouldBeGreaterThan(Rows * 20L);
        peak.ShouldBeLessThan(MaxBytes);
    }
}
