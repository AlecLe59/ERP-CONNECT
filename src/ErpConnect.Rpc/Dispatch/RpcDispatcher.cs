using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ErpConnect.Contracts;
using ErpConnect.Core.Authorization;
using Microsoft.Extensions.Logging;

namespace ErpConnect.Rpc.Dispatch;

/// <summary>Permissions effectives d'un appelant, recalculées à chaque appel.</summary>
public interface IRpcAuthorizer
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(CallerIdentity caller, CancellationToken ct);

    Task<IReadOnlySet<Role>> GetRolesAsync(CallerIdentity caller, CancellationToken ct);
}

/// <summary>Trace des refus dans le journal d'audit.</summary>
public interface IRpcAuditSink
{
    Task DeniedAsync(CallerIdentity caller, string method, string correlationId, CancellationToken ct);
}

/// <summary>
/// Routage JSON-RPC 2.0 : analyse bornée (profondeur 64), session.hello obligatoire, permission vérifiée à chaque appel,
/// erreurs sans détail interne (le détail reste dans les journaux, retrouvable par l'identifiant de corrélation).
/// </summary>
public sealed partial class RpcDispatcher
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = Protocol.MaxJsonDepth,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = Protocol.MaxJsonDepth };

    private readonly Dictionary<string, (RpcMethodDescriptor Descriptor, Func<RpcContext, JsonElement?, CancellationToken, Task<object?>> Handler)> _handlers =
        new(StringComparer.Ordinal);

    private readonly IRpcAuthorizer _authorizer;
    private readonly IRpcAuditSink _audit;
    private readonly ILogger _logger;
    private readonly string _serverVersion;
    private volatile bool _available = true;

    public RpcDispatcher(IRpcAuthorizer authorizer, IRpcAuditSink audit, ILogger<RpcDispatcher> logger, string serverVersion)
    {
        _authorizer = authorizer;
        _audit = audit;
        _logger = logger;
        _serverVersion = serverVersion;
    }

    /// <summary>Faux pendant le démarrage ou l'arrêt : les appels (hors session.hello) renvoient ENGINE_UNAVAILABLE.</summary>
    public bool Available
    {
        get => _available;
        set => _available = value;
    }

    public IEnumerable<string> RegisteredMethods => _handlers.Keys;

    public void Register<TParams, TResult>(RpcMethodDescriptor descriptor, Func<RpcContext, TParams, CancellationToken, Task<TResult>> handler)
        where TParams : class
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        if (descriptor.ParamsType != typeof(TParams) || descriptor.ResultType != typeof(TResult))
        {
            throw new InvalidOperationException($"Types incohérents avec le contrat pour {descriptor.Name}.");
        }

        if (descriptor == RpcMethods.SessionHello)
        {
            throw new InvalidOperationException("session.hello est géré par le répartiteur.");
        }

        _handlers[descriptor.Name] = (descriptor, async (ctx, raw, ct) =>
        {
            var p = DeserializeParams<TParams>(raw);
            return await handler(ctx, p, ct).ConfigureAwait(false);
        });
    }

    /// <summary>Traite un message. Renvoie la réponse sérialisée, ou null pour une notification.</summary>
    public async Task<byte[]?> HandleAsync(RpcSession session, ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(message, DocumentOptions);
        }
        catch (JsonException)
        {
            return Error(null, RpcErrorCodes.ParseError, "JSON invalide.", null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return Error(null, RpcErrorCodes.InvalidRequest, "Les lots JSON-RPC ne sont pas pris en charge.", null);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(null, RpcErrorCodes.InvalidRequest, "Requête invalide.", null);
            }

            JsonElement? id = null;
            if (root.TryGetProperty("id", out var idEl))
            {
                if (idEl.ValueKind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Null))
                {
                    return Error(null, RpcErrorCodes.InvalidRequest, "Identifiant invalide.", null);
                }

                id = idEl.ValueKind == JsonValueKind.Null ? null : idEl.Clone();
            }

            var isNotification = !root.TryGetProperty("id", out _);
            if (!root.TryGetProperty("jsonrpc", out var v) || v.ValueKind != JsonValueKind.String || v.GetString() != "2.0"
                || !root.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String)
            {
                return Error(id, RpcErrorCodes.InvalidRequest, "Requête invalide.", null);
            }

            var method = m.GetString()!;
            JsonElement? parameters = root.TryGetProperty("params", out var p) && p.ValueKind != JsonValueKind.Null ? p.Clone() : null;
            if (parameters is { ValueKind: not JsonValueKind.Object })
            {
                return Error(id, RpcErrorCodes.InvalidParams, "Les paramètres doivent être un objet nommé.", null);
            }

            var correlationId = session.NextCorrelationId();
            var response = await DispatchAsync(session, id, method, parameters, correlationId, ct).ConfigureAwait(false);
            return isNotification ? null : response;
        }
    }

    public static byte[] SerializeNotification(string method, object payload) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(payload, payload.GetType(), SerializerOptions),
        }, SerializerOptions);

    private static TParams DeserializeParams<TParams>(JsonElement? raw)
        where TParams : class
    {
        try
        {
            var value = raw is null
                ? (typeof(TParams) == typeof(EmptyParams) ? new EmptyParams() as TParams : null)
                : raw.Value.Deserialize<TParams>(SerializerOptions);
            return value ?? throw new RpcException(RpcErrorCodes.InvalidParams, "Paramètres manquants.");
        }
        catch (JsonException)
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, "Paramètres invalides.");
        }
    }

    private async Task<byte[]> DispatchAsync(RpcSession session, JsonElement? id, string method, JsonElement? parameters, string correlationId, CancellationToken ct)
    {
        try
        {
            if (!MethodFormat().IsMatch(method))
            {
                return Error(id, RpcErrorCodes.MethodNotFound, "Méthode inconnue.", correlationId);
            }

            if (method == RpcMethods.SessionHello.Name)
            {
                return Result(id, await HelloAsync(session, parameters, ct).ConfigureAwait(false));
            }

            if (!session.HelloCompleted)
            {
                return Error(id, RpcErrorCodes.Unauthenticated, "session.hello doit être appelée en premier.", correlationId);
            }

            if (!_handlers.TryGetValue(method, out var entry))
            {
                return Error(id, RpcErrorCodes.MethodNotFound, "Méthode inconnue.", correlationId);
            }

            if (!_available)
            {
                return Error(id, RpcErrorCodes.EngineUnavailable, "Moteur indisponible.", correlationId);
            }

            var permissions = await _authorizer.GetPermissionsAsync(session.Identity, ct).ConfigureAwait(false);
            if (entry.Descriptor.PermissionMode == PermissionMode.Required && !permissions.Contains(entry.Descriptor.Permission!))
            {
                LogDenied(_logger, correlationId, method, session.Identity.UserName);
                await _audit.DeniedAsync(session.Identity, method, correlationId, ct).ConfigureAwait(false);
                return Error(id, RpcErrorCodes.Forbidden, "Permission refusée.", correlationId);
            }

            var context = new RpcContext(session, correlationId, permissions);
            var result = await entry.Handler(context, parameters, ct).ConfigureAwait(false);
            return Result(id, result);
        }
        catch (RpcException ex)
        {
            if (ex.Code == RpcErrorCodes.Forbidden)
            {
                await _audit.DeniedAsync(session.Identity, method, correlationId, ct).ConfigureAwait(false);
            }

            return Error(id, ex.Code, ex.Message, correlationId, ex.Fields);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Toute erreur inattendue est journalisée et renvoyée sans détail.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailure(_logger, ex, correlationId, method);
            return Error(id, RpcErrorCodes.InternalError, "Erreur interne. Consultez les journaux du service.", correlationId);
        }
    }

    private async Task<HelloResult> HelloAsync(RpcSession session, JsonElement? parameters, CancellationToken ct)
    {
        var hello = DeserializeParams<HelloParams>(parameters);
        if (!Protocol.IsCompatible(hello.ProtocolVersion))
        {
            throw new RpcException(RpcErrorCodes.ProtocolVersionMismatch,
                $"Version de protocole {hello.ProtocolVersion} incompatible avec {Protocol.Version}. Mettez à jour l'application.");
        }

        var permissions = await _authorizer.GetPermissionsAsync(session.Identity, ct).ConfigureAwait(false);
        if (permissions.Count == 0)
        {
            // Défense en profondeur : l'ACL du pipe filtre déjà, mais un compte sans aucun rôle n'ouvre pas de session.
            throw new RpcException(RpcErrorCodes.Forbidden, "Aucun rôle ERP Connect n'est attribué à ce compte.");
        }

        var roles = await _authorizer.GetRolesAsync(session.Identity, ct).ConfigureAwait(false);
        session.HelloCompleted = true;
        session.ClientName = hello.ClientName.Length > 64 ? hello.ClientName[..64] : hello.ClientName;
        return new HelloResult(
            session.Id, Protocol.Version, _serverVersion, session.Identity.UserSid, session.Identity.UserName,
            roles.Select(r => r.ToString().ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray(),
            permissions.Order(StringComparer.Ordinal).ToArray());
    }

    private static byte[] Result(JsonElement? id, object? result) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is null ? null : JsonNode.Parse(id.Value.GetRawText()),
            ["result"] = result is null ? null : JsonSerializer.SerializeToNode(result, result.GetType(), SerializerOptions),
        }, SerializerOptions);

    private static byte[] Error(JsonElement? id, int code, string message, string? correlationId, IReadOnlyList<FieldErrorDto>? fields = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        var name = RpcErrorCodes.NameOf(code);
        if (name is not null || correlationId is not null)
        {
            error["data"] = JsonSerializer.SerializeToNode(
                new RpcErrorData(name ?? "PROTOCOL_ERROR", correlationId ?? string.Empty, fields?.ToArray()), SerializerOptions);
        }

        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is null ? null : JsonNode.Parse(id.Value.GetRawText()),
            ["error"] = error,
        }, SerializerOptions);
    }

    [GeneratedRegex("^[a-z][a-zA-Z]{0,31}\\.[a-z][a-zA-Z]{0,63}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex MethodFormat();

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "[{CorrelationId}] Refus de {Method} pour {User}")]
    private static partial void LogDenied(ILogger logger, string correlationId, string method, string user);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "[{CorrelationId}] Échec de {Method}")]
    private static partial void LogFailure(ILogger logger, Exception ex, string correlationId, string method);
}
