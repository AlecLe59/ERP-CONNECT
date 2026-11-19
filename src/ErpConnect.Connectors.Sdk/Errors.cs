namespace ErpConnect.Connectors.Sdk;

/// <summary>Classement d'une erreur : transitoire (on peut réessayer) ou permanente (inutile de réessayer).</summary>
public enum ErrorKind
{
    Transient,
    Permanent,
}

public sealed class ConnectorException : Exception
{
    public ConnectorException(ErrorKind kind, string message, Exception? inner = null, TimeSpan? retryAfter = null)
        : base(message, inner)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    public ConnectorException()
    {
    }

    public ConnectorException(string message)
        : base(message)
    {
    }

    public ConnectorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ErrorKind Kind { get; } = ErrorKind.Permanent;

    /// <summary>Délai demandé par le système distant (en-tête Retry-After).</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Classe une exception quelconque : réseau et délais = transitoire, le reste = permanent.</summary>
    public static ErrorKind Classify(Exception ex) => ex switch
    {
        ConnectorException c => c.Kind,
        TimeoutException or IOException or System.Net.Sockets.SocketException or HttpRequestException => ErrorKind.Transient,
        _ => ErrorKind.Permanent,
    };
}
