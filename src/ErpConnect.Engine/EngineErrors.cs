using ErpConnect.Core.Settings;

namespace ErpConnect.Engine;

/// <summary>Erreur métier du moteur, traduite en erreur JSON-RPC par le service.</summary>
public sealed class EngineException : Exception
{
    public EngineException(EngineErrorKind kind, string message, IReadOnlyList<FieldError>? fields = null)
        : base(message)
    {
        Kind = kind;
        Fields = fields ?? [];
    }

    public EngineException()
    {
    }

    public EngineException(string message)
        : base(message)
    {
    }

    public EngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public EngineErrorKind Kind { get; }

    public IReadOnlyList<FieldError> Fields { get; } = [];
}

public enum EngineErrorKind
{
    Validation,
    Conflict,
    NotFound,
    Forbidden,
}
