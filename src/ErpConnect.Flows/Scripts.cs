using System.Globalization;
using System.Text.Json;
using ErpConnect.Connectors.Sdk;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace ErpConnect.Flows;

/// <summary>Limites appliquées à chaque script. Fixées par le moteur, non modifiables par la définition du flux.</summary>
public sealed record ScriptLimits(TimeSpan TimeoutPerRecord, long MemoryBytes, int MaxStatements, int MaxRecursion, uint MaxArraySize)
{
    public static ScriptLimits Default { get; } = new(TimeSpan.FromMilliseconds(500), 32L * 1024 * 1024, 1_000_000, 64, 100_000);

    public const int MaxSourceLength = 64 * 1024;
}

/// <summary>
/// Bloc « script » : JavaScript (ECMAScript, interpréteur Jint) isolé.
/// Le script définit <c>function transform(record)</c> : il renvoie l'enregistrement (objet), <c>null</c> pour l'écarter,
/// ou lève une erreur (<c>throw new Error("motif")</c>) pour le rejeter.
/// Isolement : aucun accès .NET (interop désactivée), pas d'<c>eval</c> ni de <c>Function</c>, pas de modules, pas de fichiers,
/// pas de réseau, pas d'horloge réelle déterministe garantie ; mode strict ; temps, mémoire, instructions, récursion et taille
/// des tableaux bornés par enregistrement. L'enregistrement traverse la frontière en JSON : le script ne reçoit jamais d'objet .NET.
/// Un dépassement de limite arrête l'exécution (erreur permanente) au lieu de rejeter : un script défaillant ne doit pas
/// consommer le temps de tout le fichier.
/// </summary>
public sealed class ScriptStep : IDisposable
{
    private const string Bridge = "(function(t){ return function(s){ var r = t(JSON.parse(s)); if (r === null || r === undefined) { return null; } if (typeof r !== 'object' || Array.isArray(r)) { throw new TypeError('transform doit renvoyer un objet ou null'); } return JSON.stringify(r); }; })(transform)";

    private readonly Engine _engine;
    private readonly JsValue _run;

    private ScriptStep(Engine engine, JsValue run)
    {
        _engine = engine;
        _run = run;
    }

    public static ScriptStep Compile(string? source, ScriptLimits? limits = null)
    {
        limits ??= ScriptLimits.Default;
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ScriptException("Script vide.");
        }

        if (source.Length > ScriptLimits.MaxSourceLength)
        {
            throw new ScriptException($"Script trop long ({ScriptLimits.MaxSourceLength / 1024} Kio maximum).");
        }

        var engine = new Engine(o =>
        {
            o.Strict();
            o.DisableStringCompilation();
            o.LimitMemory(limits.MemoryBytes);
            o.TimeoutInterval(limits.TimeoutPerRecord);
            o.MaxStatements(limits.MaxStatements);
            o.LimitRecursion(limits.MaxRecursion);
            o.Constraints.MaxArraySize = limits.MaxArraySize;
            o.Constraints.RegexTimeout = limits.TimeoutPerRecord;
            o.Interop.Enabled = false;
            o.Interop.AllowGetType = false;
            o.Interop.AllowSystemReflection = false;
            o.Json.MaxParseDepth = Record.MaxDepth;
            o.Culture = CultureInfo.InvariantCulture;
            o.TimeZone = TimeZoneInfo.Utc;
        });
        try
        {
            engine.Execute(source);
            if (!engine.Evaluate("typeof transform").AsString().Equals("function", StringComparison.Ordinal))
            {
                throw new ScriptException("Le script doit définir function transform(record).");
            }

            var run = engine.Evaluate(Bridge);
            return new ScriptStep(engine, run);
        }
        catch (ScriptException)
        {
            engine.Dispose();
            throw;
        }
        catch (Exception ex) when (Classify(ex) is { } message)
        {
            engine.Dispose();
            throw new ScriptException(message, ex);
        }
    }

    /// <summary>Contrôle à l'enregistrement du flux : syntaxe et présence de transform.</summary>
    public static string? Validate(string? source)
    {
        try
        {
            using var step = Compile(source);
            return null;
        }
        catch (ScriptException ex)
        {
            return ex.Message;
        }
    }

    public MapResult Apply(Record record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string? json;
        try
        {
            var result = _engine.Invoke(_run, RecordJson.Serialize(record));
            json = result.IsNull() || result.IsUndefined() ? null : result.AsString();
        }
        catch (JavaScriptException ex)
        {
            return MapResult.Reject($"script : {Trim(ex.Message)}");
        }
        catch (InvalidOperationException ex)
        {
            return MapResult.Reject($"script : {Trim(ex.Message)}"); // fonction de l'hôte désactivée (modules, etc.)
        }
        catch (Exception ex) when (IsLimit(ex))
        {
            throw new ConnectorException(ErrorKind.Permanent, $"Script arrêté : {Classify(ex)}", ex);
        }

        if (json is null)
        {
            return MapResult.Filtered;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = Record.MaxDepth + 1 });
            return new MapResult(ToRecord(doc.RootElement, 0), null);
        }
        catch (Exception ex) when (ex is JsonException or ConnectorException)
        {
            return MapResult.Reject($"script : résultat invalide ({ex.Message})");
        }
    }

    public void Dispose() => _engine.Dispose();

    /// <summary>Enregistrement à partir d'un objet JSON (essai de script). Lève ScriptException si ce n'est pas un objet valide.</summary>
    public static Record ParseRecord(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 1024 * 1024)
        {
            throw new ScriptException("Enregistrement d'exemple trop volumineux (1 Mio maximum).");
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = Record.MaxDepth + 1 });
            return ToRecord(doc.RootElement, 0);
        }
        catch (Exception ex) when (ex is JsonException or ConnectorException)
        {
            throw new ScriptException($"Objet JSON attendu : {ex.Message}", ex);
        }
    }

    private static bool IsLimit(Exception ex) => ex is TimeoutException or MemoryLimitExceededException or StatementsCountOverflowException
        or RecursionDepthOverflowException or ExecutionCanceledException or ArgumentOutOfRangeException or InsufficientExecutionStackException
        || (ex is JintException && ex is not JavaScriptException);

    private static string? Classify(Exception ex) => ex switch
    {
        TimeoutException => "délai dépassé.",
        InsufficientExecutionStackException => "imbrication trop profonde.",
        MemoryLimitExceededException => "mémoire dépassée.",
        StatementsCountOverflowException => "trop d'instructions.",
        RecursionDepthOverflowException => "récursion trop profonde.",
        ExecutionCanceledException => "annulé.",
        JavaScriptException js when js.Error.IsObject() && js.Error.AsObject().Get("name").ToString() == "SyntaxError" => $"syntaxe : {Trim(js.Message)}",
        JavaScriptException js => Trim(js.Message),
        Acornima.ParseErrorException parse => $"syntaxe : {parse.Description} (ligne {parse.LineNumber}).",
        JintException j => Trim(j.Message),
        InvalidOperationException or ArgumentOutOfRangeException => Trim(ex.Message),
        _ => null,
    };

    private static string Trim(string message) => message.Length <= 300 ? message : message[..300] + "…";

    private static Record ToRecord(JsonElement e, int depth)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new ConnectorException(ErrorKind.Permanent, "objet attendu");
        }

        var r = new Record();
        foreach (var p in e.EnumerateObject())
        {
            r.Set(p.Name, ToValue(p.Value, depth + 1));
        }

        return r;
    }

    private static object? ToValue(JsonElement e, int depth)
    {
        if (depth > Record.MaxDepth)
        {
            throw new ConnectorException(ErrorKind.Permanent, $"imbrication supérieure à {Record.MaxDepth} niveaux");
        }

        return e.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.TryGetDecimal(out var d) ? d : e.GetDouble(),
            JsonValueKind.Object => ToRecord(e, depth),
            _ => e.EnumerateArray().Select(x => ToValue(x, depth + 1)).ToList(),
        };
    }
}

public sealed class ScriptException : Exception
{
    public ScriptException()
    {
    }

    public ScriptException(string message)
        : base(message)
    {
    }

    public ScriptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
