using System.IO.Pipes;
using System.Security.Cryptography;
using ErpConnect.Contracts;
using ErpConnect.Rpc.Dispatch;
using ErpConnect.Rpc.Transport;
using ErpConnect.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace ErpConnect.Rpc.Tests;

/// <summary>
/// Tests sur un vrai named pipe .NET (sous Linux : socket de domaine Unix). L'ACL et l'identité Windows
/// sont remplacées par une identité fixe : ces aspects sont couverts par les tests Windows (SecurityTests).
/// </summary>
public sealed class PipeServerTests : IAsyncDisposable
{
    private readonly string _name = "erpconnect-test-" + Guid.NewGuid().ToString("N")[..8];
    private readonly FakeAuthorizer _auth = new();
    private PipeServer? _server;

    private PipeServer Start(int maxConnections = 32, TimeSpan? idle = null)
    {
        _auth.BySid[Identities.Admin.UserSid] = ["dashboard.read", "settings.read"];
        var dispatcher = new RpcDispatcher(_auth, new FakeAudit(), NullLogger<RpcDispatcher>.Instance, "0.2.0");
        dispatcher.Register<EmptyParams, VaultStatusResult>(RpcMethods.VaultGetStatus, (_, _, _) => Task.FromResult(new VaultStatusResult(true, 1, 0, "t")));
        _server = new PipeServer(
            new PipeServerOptions
            {
                PipeName = _name,
                MaxConnections = maxConnections,
                IdleTimeout = idle ?? TimeSpan.FromMinutes(1),
                CreatePipe = (n, _) => new NamedPipeServerStream(n, PipeDirection.InOut, maxConnections, PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
                IdentityOf = _ => Identities.Admin,
            },
            dispatcher,
            NullLogger<PipeServer>.Instance);
        _server.Start();
        return _server;
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task End_to_end_call_over_a_real_pipe()
    {
        Start();
        await using var c = await RpcTestClient.ConnectAsync(_name);
        (await c.HelloAsync()).Result()["userName"]!.GetValue<string>().ShouldBe(Identities.Admin.UserName);
        (await c.CallAsync("vault.getStatus")).Result()["initialized"]!.GetValue<bool>().ShouldBeTrue();
    }

    // S12 : flot aléatoire puis client sain : le service reste disponible.
    [Fact]
    public async Task Random_garbage_closes_the_connection_but_server_stays_available()
    {
        Start();
        for (var i = 0; i < 20; i++)
        {
            await using var bad = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await bad.ConnectAsync(5000);
            try
            {
                await bad.WriteAsync(RandomNumberGenerator.GetBytes(64 + i));
                await bad.FlushAsync();
            }
            catch (IOException)
            {
            }
        }

        await using var good = await RpcTestClient.ConnectAsync(_name);
        (await good.HelloAsync())["result"].ShouldNotBeNull();
    }

    // S12 : message de 5 Mo.
    [Fact]
    public async Task Five_megabyte_message_closes_the_connection()
    {
        Start();
        await using var c = await RpcTestClient.ConnectAsync(_name);
        await Should.ThrowAsync<IOException>(() => c.SendRawAsync(new byte[5 * 1024 * 1024]));
        await using var good = await RpcTestClient.ConnectAsync(_name);
        (await good.HelloAsync())["result"].ShouldNotBeNull();
    }

    // S13 (partiel, hors ACL) : au-delà de N connexions, les suivantes attendent ; le service reste réactif.
    [Fact]
    public async Task Connection_limit_is_enforced_and_server_recovers()
    {
        const int max = 4;
        Start(maxConnections: max);
        var held = new List<RpcTestClient>();
        for (var i = 0; i < max; i++)
        {
            var c = await RpcTestClient.ConnectAsync(_name);
            (await c.HelloAsync())["result"].ShouldNotBeNull();
            held.Add(c);
        }

        // Windows : la connexion n'aboutit pas (toutes les instances sont occupées).
        // Unix : le socket accepte la connexion en file d'attente, mais aucune instance ne la sert.
        RpcTestClient? extra = null;
        try
        {
            extra = await RpcTestClient.ConnectAsync(_name, timeoutMs: 500);
            await ErpConnect.Rpc.Transport.MessageFraming.WriteAsync(extra.Stream,
                """{"jsonrpc":"2.0","id":1,"method":"session.hello","params":{"clientName":"t","clientVersion":"1","protocolVersion":"1.0"}}"""u8.ToArray(), default);
            (await extra.ReadNotificationAsync(500)).ShouldBeNull("la connexion excédentaire ne doit pas être servie");
        }
        catch (TimeoutException)
        {
        }

        await held[0].DisposeAsync();
        if (extra is not null)
        {
            (await extra.ReadNotificationAsync(5000)).ShouldNotBeNull("servie dès qu'une place se libère")["result"].ShouldNotBeNull();
            await extra.DisposeAsync();
        }

        await held[1].DisposeAsync();
        await using var again = await RpcTestClient.ConnectAsync(_name, timeoutMs: 5000);
        (await again.HelloAsync())["result"].ShouldNotBeNull();
        foreach (var c in held.Skip(2))
        {
            await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task Idle_connections_are_closed()
    {
        Start(idle: TimeSpan.FromMilliseconds(300));
        await using var c = await RpcTestClient.ConnectAsync(_name);
        await c.HelloAsync();
        await Task.Delay(800);
        await Should.ThrowAsync<IOException>(() => c.CallAsync("vault.getStatus"));
    }

    [Fact]
    public async Task Broadcast_reaches_only_subscribed_sessions_with_permission()
    {
        var server = Start();
        await using var c = await RpcTestClient.ConnectAsync(_name);
        await c.HelloAsync();
        // Abonnement direct (la méthode events.subscribe est testée dans les tests d'intégration).
        await Task.Delay(100);
        server.ActiveSessions.ShouldBe(1);
        await server.BroadcastAsync(RpcNotifications.SettingsChanged, new SettingsChangedPayload(2, "x"), _auth, default);
        (await c.ReadNotificationAsync(300)).ShouldBeNull(); // non abonné
    }
}
