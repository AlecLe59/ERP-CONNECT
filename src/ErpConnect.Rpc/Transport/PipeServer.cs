using System.Collections.Concurrent;
using System.IO.Pipes;
using ErpConnect.Contracts;
using ErpConnect.Core.Authorization;
using ErpConnect.Rpc.Dispatch;
using Microsoft.Extensions.Logging;

namespace ErpConnect.Rpc.Transport;

public sealed class PipeServerOptions
{
    public string PipeName { get; init; } = Protocol.PipeName;

    public int MaxConnections { get; init; } = Protocol.MaxConnections;

    public TimeSpan IdleTimeout { get; init; } = Protocol.IdleTimeout;

    public int MaxMessageBytes { get; init; } = Protocol.MaxMessageBytes;

    /// <summary>Crée une instance du pipe. <c>first</c> vaut vrai pour la toute première (détection d'un faux serveur).</summary>
    public required Func<string, bool, NamedPipeServerStream> CreatePipe { get; init; }

    /// <summary>Identité Windows du client connecté (jeton du pipe).</summary>
    public required Func<NamedPipeServerStream, CallerIdentity> IdentityOf { get; init; }
}

/// <summary>Le pipe existe déjà : un autre processus l'a créé avant le service (S4). Le service doit refuser de démarrer.</summary>
public sealed class PipeSquattingException : Exception
{
    public PipeSquattingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PipeSquattingException()
    {
    }

    public PipeSquattingException(string message)
        : base(message)
    {
    }
}

/// <summary>Serveur named pipe : au plus N connexions simultanées, une session par connexion.</summary>
public sealed partial class PipeServer : IAsyncDisposable
{
    private readonly PipeServerOptions _options;
    private readonly RpcDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, (RpcSession Session, RpcConnection Connection)> _sessions = new();
    private readonly CancellationTokenSource _stopping = new();
    private NamedPipeServerStream? _firstInstance;
    private Task? _acceptLoop;

    public PipeServer(PipeServerOptions options, RpcDispatcher dispatcher, ILogger<PipeServer> logger)
    {
        _options = options;
        _dispatcher = dispatcher;
        _logger = logger;
        _slots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
    }

    public int ActiveSessions => _sessions.Count;

    /// <summary>Crée la première instance (échoue si le nom est déjà pris) puis lance la boucle d'acceptation.</summary>
    public void Start()
    {
        try
        {
            _firstInstance = _options.CreatePipe(_options.PipeName, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PipeSquattingException(
                $"Le pipe {_options.PipeName} existe déjà : un autre processus l'a créé. Démarrage refusé.", ex);
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
    }

    /// <summary>Diffuse une notification aux sessions abonnées qui ont la permission requise.</summary>
    public async Task BroadcastAsync(RpcNotificationDescriptor notification, object payload, IRpcAuthorizer authorizer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(authorizer);
        foreach (var (_, (session, connection)) in _sessions)
        {
            if (!session.Subscriptions.ContainsKey(notification.Name))
            {
                continue;
            }

            var permissions = await authorizer.GetPermissionsAsync(session.Identity, ct).ConfigureAwait(false);
            if (!permissions.Contains(notification.Permission))
            {
                continue;
            }

            try
            {
                await connection.SendNotificationAsync(notification.Name, payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Connexion en cours de fermeture : ignorée.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_firstInstance is not null)
        {
            await _firstInstance.DisposeAsync().ConfigureAwait(false);
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var (_, (_, connection)) in _sessions)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
        _slots.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var next = _firstInstance;
        while (!ct.IsCancellationRequested)
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = next ?? _options.CreatePipe(_options.PipeName, false);
                next = null;
                _firstInstance = null;
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                _slots.Release();
                return;
            }
            catch (IOException ex)
            {
                // Client parti avant l'acceptation (ERROR_NO_DATA sous Windows) : l'instance doit être libérée,
                // sinon les instances fuient jusqu'au plafond du pipe et plus aucun client ne peut se connecter.
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                _slots.Release();
                LogAcceptError(_logger, ex.Message);
                await Task.Delay(100, ct).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string? key = null;
        RpcConnection? connection = null;
        try
        {
            CallerIdentity identity;
            try
            {
                identity = _options.IdentityOf(pipe);
            }
#pragma warning disable CA1031 // Identité illisible : connexion refusée, sans détail au client.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogIdentityFailure(_logger, ex.Message);
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }

            connection = new RpcConnection(pipe, _dispatcher, _logger, _options.IdleTimeout, _options.MaxMessageBytes);
            var conn = connection;
            var session = new RpcSession(identity, (method, payload, token) => conn.SendNotificationAsync(method, payload, token));
            key = session.Id;
            _sessions[key] = (session, connection);
            LogConnected(_logger, session.Id, identity.UserName);
            await connection.RunAsync(session, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Une connexion défaillante ne doit jamais arrêter le serveur.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSessionError(_logger, ex);
        }
        finally
        {
            if (key is not null)
            {
                _sessions.TryRemove(key, out _);
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }

            _slots.Release();
        }
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "Session {SessionId} ouverte pour {User}")]
    private static partial void LogConnected(ILogger logger, string sessionId, string user);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning, Message = "Identité du client illisible : {Reason}")]
    private static partial void LogIdentityFailure(ILogger logger, string reason);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Error, Message = "Erreur de session")]
    private static partial void LogSessionError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Warning, Message = "Erreur d'acceptation : {Reason}")]
    private static partial void LogAcceptError(ILogger logger, string reason);
}
