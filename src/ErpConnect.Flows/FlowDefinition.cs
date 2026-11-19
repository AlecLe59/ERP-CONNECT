using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErpConnect.Flows;

#pragma warning disable CA1819 // Forme sérialisée.

public sealed record OptionEntry(string Key, string Value);

public sealed record SourceSpec(string ConnectionId, string Resource, OptionEntry[] Options);

/// <summary>mode : createNew | overwrite.</summary>
public sealed record TargetSpec(string ConnectionId, string Resource, string Mode, OptionEntry[] Options);

/// <summary>Correspondance d'un champ cible : expression évaluée sur l'enregistrement source.</summary>
public sealed record FieldMap(string Target, string Expression);

/// <summary>mode : identity (copie telle quelle) | explicit (seuls les champs listés).</summary>
public sealed record MappingSpec(string Mode, FieldMap[] Fields);

/// <summary>
/// Bloc de transformation appliqué avant la correspondance, dans l'ordre.
/// kind : filter (expression booléenne, faux = enregistrement écarté), set (field = expression),
/// rename (field → target), remove (field, liste séparée par des virgules), validate (expression fausse = rejet avec message),
/// script (JavaScript isolé dans expression : function transform(record), voir ScriptStep).
/// </summary>
public sealed record StepSpec(string Kind, string? Field = null, string? Expression = null, string? Target = null, string? Message = null);

/// <summary>Définition versionnée d'un flux, stockée en JSON (camelCase).</summary>
public sealed record FlowDefinition(SourceSpec Source, TargetSpec Target, MappingSpec Mapping, int BatchSize = 1000, int MaxRejects = 1000, StepSpec[]? Steps = null, string? DedupKey = null)
{
    public IReadOnlyList<StepSpec> AllSteps => Steps ?? [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        MaxDepth = 32,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static FlowDefinition Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FlowDefinition>(json, Json) ?? throw new FlowValidationException([new("definition", "Définition vide.")]);
        }
        catch (JsonException ex)
        {
            throw new FlowValidationException([new("definition", $"JSON invalide : {ex.Message}")]);
        }
    }

    public IReadOnlyList<FlowError> Validate()
    {
        var errors = new List<FlowError>();
        if (string.IsNullOrWhiteSpace(Source.ConnectionId))
        {
            errors.Add(new("source.connectionId", "Connexion source obligatoire."));
        }

        if (string.IsNullOrWhiteSpace(Source.Resource))
        {
            errors.Add(new("source.resource", "Ressource source obligatoire."));
        }

        if (string.IsNullOrWhiteSpace(Target.ConnectionId))
        {
            errors.Add(new("target.connectionId", "Connexion cible obligatoire."));
        }

        if (string.IsNullOrWhiteSpace(Target.Resource))
        {
            errors.Add(new("target.resource", "Ressource cible obligatoire."));
        }

        if (Target.Mode is not ("createNew" or "overwrite"))
        {
            errors.Add(new("target.mode", "Valeurs admises : createNew, overwrite."));
        }

        if (BatchSize is < 1 or > 50_000)
        {
            errors.Add(new("batchSize", "Entre 1 et 50 000."));
        }

        if (MaxRejects is < 1 or > 1_000_000)
        {
            errors.Add(new("maxRejects", "Entre 1 et 1 000 000."));
        }

        if (!string.IsNullOrWhiteSpace(DedupKey) && ExpressionCompiler.TryCompile(DedupKey, out var dedupError) is null)
        {
            errors.Add(new FlowError("dedupKey", dedupError!));
        }

        if (AllSteps.Count > 100)
        {
            errors.Add(new("steps", "100 blocs maximum."));
        }

        for (var i = 0; i < AllSteps.Count; i++)
        {
            errors.AddRange(RecordPipeline.ValidateStep(AllSteps[i]).Select(e => new FlowError($"steps[{i}].{e.Field}", e.Message)));
        }

        switch (Mapping.Mode)
        {
            case "identity":
                break;
            case "explicit":
                if (Mapping.Fields.Length == 0)
                {
                    errors.Add(new("mapping.fields", "Au moins un champ."));
                }

                foreach (var dup in Mapping.Fields.GroupBy(f => f.Target, StringComparer.Ordinal).Where(g => g.Count() > 1))
                {
                    errors.Add(new("mapping.fields", $"Champ cible en double : {dup.Key}."));
                }

                for (var i = 0; i < Mapping.Fields.Length; i++)
                {
                    var f = Mapping.Fields[i];
                    if (string.IsNullOrWhiteSpace(f.Target))
                    {
                        errors.Add(new($"mapping.fields[{i}].target", "Nom obligatoire."));
                    }

                    if (ExpressionCompiler.TryCompile(f.Expression, out var error) is null)
                    {
                        errors.Add(new($"mapping.fields[{i}].expression", error!));
                    }
                }

                break;
            default:
                errors.Add(new("mapping.mode", "Valeurs admises : identity, explicit."));
                break;
        }

        return errors;
    }

    public static Dictionary<string, string> ToDictionary(OptionEntry[] options) =>
        options.GroupBy(o => o.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
}

public sealed record FlowError(string Field, string Message);

public sealed class FlowValidationException : Exception
{
    public FlowValidationException(IReadOnlyList<FlowError> errors)
        : base(string.Join(" ", errors.Select(e => $"{e.Field} : {e.Message}")))
    {
        Errors = errors;
    }

    public FlowValidationException()
    {
        Errors = [];
    }

    public FlowValidationException(string message)
        : base(message)
    {
        Errors = [];
    }

    public FlowValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [];
    }

    public IReadOnlyList<FlowError> Errors { get; }
}
