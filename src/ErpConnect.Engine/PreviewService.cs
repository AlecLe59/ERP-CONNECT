using ErpConnect.Connectors.Sdk;
using ErpConnect.Core.Settings;
using ErpConnect.Flows;

namespace ErpConnect.Engine;

/// <summary>Ligne d'aperçu : enregistrement source, résultat transformé et sort de l'enregistrement.</summary>
public sealed record PreviewRow(string SourceJson, string? OutputJson, RecordFate Fate, string? Reason);

/// <summary>Résultat d'aperçu : lignes et champs rencontrés dans la source.</summary>
public sealed record PreviewResult(IReadOnlyList<PreviewRow> Rows, IReadOnlyList<string> SourceFields);

/// <summary>
/// Aperçu d'un flux sur un échantillon de la source, sans écriture vers la cible.
/// Seule la source est lue ; aucune exécution n'est créée et rien n'est écrit.
/// </summary>
public sealed class PreviewService
{
    public const int MaxLimit = 200;
    private const int MaxJsonLength = 16 * 1024;
    private readonly ConnectionService _connections;

    public PreviewService(ConnectionService connections) => _connections = connections;

    public async Task<PreviewResult> PreviewAsync(FlowDefinition definition, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (limit is < 1 or > MaxLimit)
        {
            throw new EngineException(EngineErrorKind.Validation, $"Taille d'échantillon entre 1 et {MaxLimit}.", [new FieldError("limit", $"Entre 1 et {MaxLimit}.")]);
        }

        RecordPipeline pipeline;
        try
        {
            pipeline = RecordPipeline.Compile(definition);
        }
        catch (FlowValidationException ex)
        {
            throw new EngineException(EngineErrorKind.Validation, "Transformation invalide.", ex.Errors.Select(e => new FieldError(e.Field, e.Message)).ToList());
        }

        var (connector, settings) = await _connections.ResolveAsync(definition.Source.ConnectionId, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var rows = new List<PreviewRow>(limit);
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reader = connector.CreateReader(settings, new ReadRequest(definition.Source.Resource, limit, FlowDefinition.ToDictionary(definition.Source.Options)));
        await using (reader.ConfigureAwait(false))
        {
            await foreach (var batch in reader.ReadAsync(null, timeout.Token).ConfigureAwait(false))
            {
                foreach (var record in batch.Records)
                {
                    foreach (var name in record.Names)
                    {
                        if (seen.Add(name))
                        {
                            fields.Add(name);
                        }
                    }

                    var r = pipeline.Process(record);
                    rows.Add(new PreviewRow(
                        RecordJson.Serialize(record, MaxJsonLength),
                        r.Output is null ? null : RecordJson.Serialize(r.Output, MaxJsonLength),
                        r.Fate,
                        r.RejectReason));
                    if (rows.Count >= limit)
                    {
                        return new PreviewResult(rows, fields);
                    }
                }
            }
        }

        return new PreviewResult(rows, fields);
    }
}
