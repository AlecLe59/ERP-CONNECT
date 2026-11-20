using ErpConnect.Contracts;
using ErpConnect.Rpc.Dispatch;
using Microsoft.Extensions.Logging;

namespace ErpConnect.Rpc.Transport;

/// <summary>Boucle de lecture d'une connexion : messages traités dans l'ordre, écritures sérialisées, délai d'inactivité.</summary>
public sealed partial class RpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly RpcDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxMessageBytes;

    public RpcConnection(Stream stream, RpcDispatcher dispatcher, ILogger logger, TimeSpan? idleTimeout = null, int? maxMessageBytes = null)
    {
        _stream = stream;
        _dispatcher = dispatcher;
        _logger = logger;
        _idleTimeout = idleTimeout ?? Protocol.IdleTimeout;
        _maxMessageBytes = maxMessageBytes ?? Protocol.MaxMessageBytes;
    }

    public async Task SendNotificationAsync(string method, object payload, CancellationToken ct)
    {
        var bytes = RpcDispatcher.SerializeNotification(method, payload);
        await WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Traite les messages jusqu'à la fermeture, une erreur de transport, l'inactivité ou l'annulation.</summary>
    public async Task RunAsync(RpcSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        while (!ct.IsCancellationRequested)
        {
            byte[]? message;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(_idleTimeout);
                try
                {
                    message = await MessageFraming.ReadAsync(_stream, _maxMessageBytes, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    LogIdle(_logger, session.Id);
                    return;
                }
                catch (FramingException ex)
                {
                    LogFraming(_logger, session.Id, ex.Message);
                    return;
                }
                catch (IOException)
                {
                    return;
                }
            }

            if (message is null)
            {
                return;
            }

            var response = await _dispatcher.HandleAsync(session, message, ct).ConfigureAwait(false);
            if (response is not null)
            {
                try
                {
                    await WriteAsync(response, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await MessageFraming.WriteAsync(_stream, bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "Session {SessionId} fermée après inactivité")]
    private static partial void LogIdle(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning, Message = "Session {SessionId} fermée : {Reason}")]
    private static partial void LogFraming(ILogger logger, string sessionId, string reason);
}
