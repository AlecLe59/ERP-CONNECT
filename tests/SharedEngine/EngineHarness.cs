using System.Collections.Concurrent;
using System.IO.Pipes;
using ErpConnect.Core.Authorization;
using ErpConnect.Engine;
using ErpConnect.Rpc.Dispatch;
using ErpConnect.Rpc.Transport;
using ErpConnect.Service;
using ErpConnect.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace ErpConnect.Tests.Engine;

/// <summary>
/// Moteur complet (migrations, coffre, droits, audit, RPC) sur un vrai named pipe, avec un protecteur de clé
/// de test et une identité choisie par le test pour chaque connexion. Sous Windows, le service réel utilise
/// DPAPI, l'ACL du pipe et le jeton du client : ces trois éléments sont couverts par les tests « Windows installé ».
/// </summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    private readonly ConcurrentQueue<CallerIdentity> _nextIdentities = new();
    private PipeServer? _server;

    private EngineHarness(string root, FakeClock clock)
    {
        Root = root;
        Clock = clock;
        PipeName = "erpconnect-it-" + Guid.NewGuid().ToString("N")[..10];
    }

    public string Root { get; }

    public FakeClock Clock { get; }

    public string PipeName { get; }

    public FakePrincipalResolver Principals { get; } = new();

    public EngineRuntime Runtime { get; private set; } = null!;

    public RpcDispatcher Dispatcher { get; private set; } = null!;

    public PipeServer Server => _server!;

    public static async Task<EngineHarness> StartAsync(string root, FakeClock? clock = null, int maxConnections = 32)
    {
        var h = new EngineHarness(root, clock ?? new FakeClock());
        await h.StartCoreAsync(maxConnections);
        return h;
    }

    public async Task<RpcTestClient> ConnectAsync(CallerIdentity who, bool hello = true)
    {
        _nextIdentities.Enqueue(who);
        var client = await RpcTestClient.ConnectAsync(PipeName);
        if (hello)
        {
            var r = await client.HelloAsync();
            if (r["result"] is null)
            {
                throw new InvalidOperationException("hello refusé : " + r.ToJsonString());
            }
        }

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        await Runtime.DisposeAsync();
    }

    private async Task StartCoreAsync(int maxConnections)
    {
        Runtime = new EngineRuntime(new EnginePaths(Root), _ => new TestKeyProtector(), Principals, Clock, "0.2.0-test");
        await Runtime.StartAsync(default);
        Dispatcher = new RpcDispatcher(new RuntimeAuthorizer(Runtime), new RuntimeAuditSink(Runtime), NullLogger<RpcDispatcher>.Instance, Runtime.Version);
        RpcEndpoints.Register(Dispatcher, Runtime);
        _server = new PipeServer(
            new PipeServerOptions
            {
                PipeName = PipeName,
                MaxConnections = maxConnections,
                CreatePipe = (n, _) => new NamedPipeServerStream(n, PipeDirection.InOut, maxConnections, PipeTransmissionMode.Byte, PipeOptions.Asynchronous),
                IdentityOf = _ => _nextIdentities.TryDequeue(out var id) ? id : throw new InvalidOperationException("Aucune identité préparée."),
            },
            Dispatcher,
            NullLogger<PipeServer>.Instance);
        var authorizer = new RuntimeAuthorizer(Runtime);
        Runtime.Events.OnSettingsChanged += (v, by) =>
            _ = _server.BroadcastAsync(ErpConnect.Contracts.RpcNotifications.SettingsChanged, new ErpConnect.Contracts.SettingsChangedPayload(v, by), authorizer, default);
        Runtime.Events.OnRunChanged += (id, flow, status) =>
            _ = _server.BroadcastAsync(ErpConnect.Contracts.RpcNotifications.RunChanged, new ErpConnect.Contracts.RunChangedPayload(id, flow, RpcEndpoints.StatusName(status)), authorizer, default);
        _server.Start();
        Runtime.Status.SetState(EngineState.Running);
    }
}
