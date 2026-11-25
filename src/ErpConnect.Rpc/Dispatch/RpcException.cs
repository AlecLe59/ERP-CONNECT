using ErpConnect.Contracts;

namespace ErpConnect.Rpc.Dispatch;

/// <summary>Erreur métier renvoyée au client. Le message ne doit contenir ni secret, ni chemin sensible, ni trace.</summary>
public sealed class RpcException : Exception
{
    public RpcException(int code, string message, IReadOnlyList<FieldErrorDto>? fields = null)
        : base(message)
    {
        Code = code;
        Fields = fields;
    }

    public RpcException()
    {
    }

    public RpcException(string message)
        : base(message)
    {
    }

    public RpcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int Code { get; } = RpcErrorCodes.InternalError;

    public IReadOnlyList<FieldErrorDto>? Fields { get; }

    public static RpcException NotFound(string what) => new(RpcErrorCodes.NotFound, $"{what} introuvable.");

    public static RpcException Conflict(string message) => new(RpcErrorCodes.Conflict, message);

    public static RpcException Validation(IReadOnlyList<FieldErrorDto> fields) =>
        new(RpcErrorCodes.ValidationFailed, "Paramètres invalides.", fields);
}
