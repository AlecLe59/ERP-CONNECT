namespace ErpConnect.Contracts;

/// <summary>Constantes du protocole JSON-RPC interface ↔ moteur. Version indépendante de celle du logiciel.</summary>
public static class Protocol
{
    public const int Major = 1;
    public const int Minor = 1;
    public const string Version = "1.6";

    /// <summary>Nom du named pipe (\\.\pipe\ERPConnect.Engine).</summary>
    public const string PipeName = "ERPConnect.Engine";

    public const int MaxMessageBytes = 4 * 1024 * 1024;
    public const int MaxJsonDepth = 64;
    public const int MaxConnections = 32;
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromMinutes(30);

    /// <summary>Nom du service et du compte virtuel associé.</summary>
    public const string ServiceName = "ERPConnectEngine";
    public const string ServiceAccount = @"NT SERVICE\ERPConnectEngine";

    /// <summary>Indique si deux versions de protocole ont la même version majeure.</summary>
    public static bool IsCompatible(string? clientVersion)
    {
        if (string.IsNullOrWhiteSpace(clientVersion))
        {
            return false;
        }

        var parts = clientVersion.Split('.');
        return parts.Length >= 1
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            && major == Major;
    }
}

/// <summary>Codes d'erreur JSON-RPC.</summary>
public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public const int Unauthenticated = -32001;
    public const int Forbidden = -32002;
    public const int NotFound = -32003;
    public const int Conflict = -32004;
    public const int ValidationFailed = -32005;
    public const int ProtocolVersionMismatch = -32006;
    public const int EngineUnavailable = -32010;
    public const int EntitlementDenied = -32011;

    public static string? NameOf(int code) => code switch
    {
        Unauthenticated => "UNAUTHENTICATED",
        Forbidden => "FORBIDDEN",
        NotFound => "NOT_FOUND",
        Conflict => "CONFLICT",
        ValidationFailed => "VALIDATION_FAILED",
        ProtocolVersionMismatch => "PROTOCOL_VERSION_MISMATCH",
        EngineUnavailable => "ENGINE_UNAVAILABLE",
        EntitlementDenied => "ENTITLEMENT_DENIED",
        _ => null,
    };
}

/// <summary>Marque un champ contenant un secret. Interdit dans tout objet de réponse (vérifié par les tests d'architecture).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class SecretAttribute : Attribute;
