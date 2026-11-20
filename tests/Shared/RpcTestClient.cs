using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErpConnect.Contracts;
using ErpConnect.Rpc.Transport;

namespace ErpConnect.Tests.Shared;

/// <summary>Client JSON-RPC de test sur un flux (pipe réel ou mémoire).</summary>
internal sealed class RpcTestClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private int _id;

    public RpcTestClient(Stream stream) => _stream = stream;

    public static async Task<RpcTestClient> ConnectAsync(string pipeName, int timeoutMs = 5000)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeoutMs);
        return new RpcTestClient(client);
    }

    public List<JsonNode> Notifications { get; } = [];

    public async Task<JsonNode> CallAsync(string method, object? @params = null)
    {
        var id = Interlocked.Increment(ref _id);
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null)
        {
            req["params"] = JsonSerializer.SerializeToNode(@params, @params.GetType(), ErpConnect.Rpc.Dispatch.RpcDispatcher.SerializerOptions);
        }

        return await SendRawAsync(Encoding.UTF8.GetBytes(req.ToJsonString()));
    }

    public Task<JsonNode> HelloAsync(string protocolVersion = Protocol.Version) =>
        CallAsync("session.hello", new HelloParams("tests", "0.0.0", protocolVersion));

    public async Task<JsonNode> SendRawAsync(byte[] payload)
    {
        await MessageFraming.WriteAsync(_stream, payload, default);
        while (true)
        {
            var bytes = await MessageFraming.ReadAsync(_stream, Protocol.MaxMessageBytes, default)
                ?? throw new IOException("Connexion fermée par le serveur.");
            var node = JsonNode.Parse(bytes)!;
            if (node["id"] is null && node["method"] is not null)
            {
                Notifications.Add(node);
                continue;
            }

            return node;
        }
    }

    public async Task<JsonNode?> ReadNotificationAsync(int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var bytes = await MessageFraming.ReadAsync(_stream, Protocol.MaxMessageBytes, cts.Token);
            return bytes is null ? null : JsonNode.Parse(bytes);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public Stream Stream => _stream;

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

internal static class RpcAssert
{
    public static int ErrorCode(this JsonNode response) => response["error"]?["code"]?.GetValue<int>()
        ?? throw new InvalidOperationException($"Pas d'erreur dans : {response.ToJsonString()}");

    public static JsonNode Result(this JsonNode response) => response["result"]
        ?? throw new InvalidOperationException($"Pas de résultat dans : {response.ToJsonString()}");
}
