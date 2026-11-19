using ErpConnect.Security.Logging;
using Serilog.Events;
using Serilog.Formatting;

namespace ErpConnect.Service;

/// <summary>Applique le masquage des secrets au texte final de chaque événement de journal, exceptions comprises.</summary>
internal sealed class RedactingFormatter : ITextFormatter
{
    private readonly ITextFormatter _inner;
    private readonly SecretRedactor _redactor;

    public RedactingFormatter(ITextFormatter inner, SecretRedactor redactor)
    {
        _inner = inner;
        _redactor = redactor;
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var buffer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);
        output.Write(_redactor.Redact(buffer.ToString()));
    }
}
