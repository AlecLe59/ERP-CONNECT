using System.Collections.Concurrent;
using ErpConnect.Core.Authorization;

namespace ErpConnect.Rpc.Dispatch;

/// <summary>État d'une connexion cliente. L'identité vient du jeton Windows, jamais du contenu des messages.</summary>
public sealed class RpcSession
{
    private long _requestCounter;

    public RpcSession(CallerIdentity identity, Func<string, object, CancellationToken, Task>? notify = null)
    {
        Identity = identity;
        Notify = notify ?? ((_, _, _) => Task.CompletedTask);
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    public CallerIdentity Identity { get; }

    public bool HelloCompleted { get; internal set; }

    public string? ClientName { get; internal set; }

    public ConcurrentDictionary<string, byte> Subscriptions { get; } = new(StringComparer.Ordinal);

    /// <summary>Envoie une notification JSON-RPC au client (méthode, contenu).</summary>
    public Func<string, object, CancellationToken, Task> Notify { get; }

    internal string NextCorrelationId() => $"{Id}-{Interlocked.Increment(ref _requestCounter)}";
}

/// <summary>Contexte passé aux gestionnaires de méthodes.</summary>
public sealed record RpcContext(RpcSession Session, string CorrelationId, IReadOnlySet<string> Permissions)
{
    public CallerIdentity Caller => Session.Identity;
}
