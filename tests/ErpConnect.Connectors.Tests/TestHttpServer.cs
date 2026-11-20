using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace ErpConnect.Connectors.Tests;

/// <summary>Vrai serveur HTTP local (HttpListener) : les tests REST passent par la pile réseau réelle.</summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerRequest, string, (int Status, string Body, Dictionary<string, string>? Headers)> _handler;
    private readonly Task _loop;

    private readonly bool _record;

    public TestHttpServer(Func<HttpListenerRequest, string, (int Status, string Body, Dictionary<string, string>? Headers)> handler, bool record = true)
    {
        _handler = handler;
        _record = record;
        Port = Samples.FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private int _count;

    public int Port { get; }

    public int RequestCount => Volatile.Read(ref _count);

    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public ConcurrentQueue<(string Method, string Path, string Body, string? IdempotencyKey, string? Authorization)> Requests { get; } = new();

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                Interlocked.Increment(ref _count);
                if (_record)
                {
                    Requests.Enqueue((ctx.Request.HttpMethod, ctx.Request.Url!.PathAndQuery, body, ctx.Request.Headers["Idempotency-Key"], ctx.Request.Headers["Authorization"]));
                }

                var (status, response, headers) = _handler(ctx.Request, body);
                ctx.Response.StatusCode = status;
                foreach (var (k, v) in headers ?? [])
                {
                    ctx.Response.Headers[k] = v;
                }

                var bytes = Encoding.UTF8.GetBytes(response);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            });
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }
}
