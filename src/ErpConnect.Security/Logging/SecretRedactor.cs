using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ErpConnect.Security.Logging;

/// <summary>
/// Masque dans un texte (message de journal, exception) les valeurs secrètes connues et les motifs usuels
/// (password=…, token: …). Utilisé par l'enrichisseur Serilog du service.
/// </summary>
public sealed partial class SecretRedactor
{
    public const string Mask = "***";

    private readonly ConcurrentDictionary<string, byte> _known = new(StringComparer.Ordinal);

    /// <summary>Enregistre une valeur à masquer (au moment où elle est déchiffrée ou saisie).</summary>
    public void Register(string secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
        {
            _known.TryAdd(secret, 0);
        }
    }

    public void Forget(string secret) => _known.TryRemove(secret, out _);

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var secret in _known.Keys.OrderByDescending(k => k.Length))
        {
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return KeyValuePattern().Replace(result, m => m.Groups["k"].Value + m.Groups["sep"].Value + Mask);
    }

    [GeneratedRegex(@"(?<k>\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret)\b)(?<sep>\s*[=:]\s*)(?<v>""[^""]*""|'[^']*'|[^\s;,&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex KeyValuePattern();
}
