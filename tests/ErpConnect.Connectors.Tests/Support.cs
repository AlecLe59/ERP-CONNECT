using System.Net;
using System.Net.Sockets;
using System.Text;
using ErpConnect.Connectors.Sdk;
using Record = ErpConnect.Connectors.Sdk.Record;

namespace ErpConnect.Connectors.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "erpconnect-conn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Samples
{
    /// <summary>Jeu d'essai commun : séparateurs, guillemets, retours ligne, accents, vides.</summary>
    public static List<Record> Tricky() =>
    [
        Record.From(("code", "C001"), ("nom", "Dupont; \"Jean\""), ("ville", "Lille")),
        Record.From(("code", "C002"), ("nom", "Ligne\nmultiple"), ("ville", "Évry-Courcouronnes")),
        Record.From(("code", "C003"), ("nom", ""), ("ville", "日本")),
        Record.From(("code", "C004"), ("nom", "a,b|c\td"), ("ville", "Saint-Étienne")),
    ];

    public static async Task<List<Record>> ReadAll(IRecordFormat format, Stream s, Dictionary<string, string>? options = null)
    {
        var list = new List<Record>();
        await foreach (var r in format.ReadAsync(s, options ?? [], CancellationToken.None))
        {
            list.Add(r);
        }

        return list;
    }

    public static async Task<byte[]> WriteAll(IRecordFormat format, IEnumerable<Record> records, Dictionary<string, string>? options = null)
    {
        using var ms = new MemoryStream();
        await using (var w = format.CreateWriter(ms, options ?? []))
        {
            foreach (var r in records)
            {
                await w.WriteAsync(r, CancellationToken.None);
            }

            await w.FinishAsync(CancellationToken.None);
        }

        return ms.ToArray();
    }

    public static Stream Utf8(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    public static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}

internal sealed class RecordingDelay
{
    public List<TimeSpan> Waits { get; } = [];

    public Task Delay(TimeSpan t, CancellationToken ct)
    {
        lock (Waits)
        {
            Waits.Add(t);
        }

        return Task.CompletedTask;
    }
}
