namespace ErpConnect.Connectors.Sdk;

[Flags]
public enum ConnectorCapabilities
{
    None = 0,
    Read = 1,
    Write = 2,
    Discover = 4,
    FileTransport = 8,
}

public enum ConfigFieldType
{
    Text,
    Number,
    Boolean,
    Choice,
    Url,
    Path,
}

/// <summary>Champ du schéma de configuration. Un champ secret n'est jamais relu : il est stocké dans le coffre.</summary>
public sealed record ConfigField(
    string Name,
    string Label,
    ConfigFieldType Type,
    bool Required = false,
    bool Secret = false,
    string? Default = null,
    IReadOnlyList<string>? Choices = null,
    string? Help = null);

public sealed record ConnectorManifest(
    string Id,
    string Name,
    string Version,
    ConnectorCapabilities Capabilities,
    IReadOnlyList<ConfigField> ConfigSchema,
    string Description);

public sealed record SettingError(string Field, string Message);

/// <summary>Configuration résolue d'une connexion : valeurs non secrètes et secrets déchiffrés par le moteur.</summary>
public sealed class ConnectorSettings
{
    private readonly IReadOnlyDictionary<string, string?> _values;
    private readonly IReadOnlyDictionary<string, string> _secrets;

    public ConnectorSettings(IReadOnlyDictionary<string, string?> values, IReadOnlyDictionary<string, string>? secrets = null)
    {
        _values = values ?? throw new ArgumentNullException(nameof(values));
        _secrets = secrets ?? new Dictionary<string, string>();
    }

    public string? Get(string name) => _values.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public string Require(string name) => Get(name) ?? throw new ConnectorException(ErrorKind.Permanent, $"Paramètre obligatoire manquant : {name}.");

    public int GetInt(string name, int fallback) =>
        int.TryParse(Get(name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public bool GetBool(string name, bool fallback = false) => Get(name) is { } v ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : fallback;

    public string? GetSecret(string name) => _secrets.TryGetValue(name, out var v) ? v : null;

    public IEnumerable<string> Names => _values.Keys;

    /// <summary>Valide les valeurs selon le schéma (présence, type, choix).</summary>
    public IReadOnlyList<SettingError> ValidateAgainst(IReadOnlyList<ConfigField> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var errors = new List<SettingError>();
        foreach (var f in schema)
        {
            var value = f.Secret ? GetSecret(f.Name) : Get(f.Name);
            if (value is null)
            {
                if (f.Required)
                {
                    errors.Add(new SettingError(f.Name, "Obligatoire."));
                }

                continue;
            }

            switch (f.Type)
            {
                case ConfigFieldType.Number when !long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _):
                    errors.Add(new SettingError(f.Name, "Nombre entier attendu."));
                    break;
                case ConfigFieldType.Boolean when value is not ("true" or "false"):
                    errors.Add(new SettingError(f.Name, "true ou false attendu."));
                    break;
                case ConfigFieldType.Choice when f.Choices is not null && !f.Choices.Contains(value, StringComparer.Ordinal):
                    errors.Add(new SettingError(f.Name, $"Valeurs admises : {string.Join(", ", f.Choices)}."));
                    break;
                case ConfigFieldType.Url when !Uri.TryCreate(value, UriKind.Absolute, out _):
                    errors.Add(new SettingError(f.Name, "URL absolue attendue."));
                    break;
            }
        }

        foreach (var name in _values.Keys)
        {
            if (!schema.Any(f => string.Equals(f.Name, name, StringComparison.Ordinal)))
            {
                errors.Add(new SettingError(name, "Paramètre inconnu."));
            }
        }

        return errors;
    }
}

public sealed record ConnectionTestResult(bool Success, string Message, TimeSpan Duration);

public sealed record FieldInfo(string Name, string Type, IReadOnlyList<FieldInfo>? Children = null);

/// <summary>Structure découverte : ressources disponibles (fichiers, tables, points d'accès) et champs d'un échantillon.</summary>
public sealed record DataStructure(IReadOnlyList<string> Resources, IReadOnlyList<FieldInfo> Fields);

/// <summary>Lecture : ressource visée (fichier, chemin d'API), taille des lots, options de format.</summary>
public sealed record ReadRequest(string Resource, int BatchSize = 1000, IReadOnlyDictionary<string, string>? Options = null)
{
    public string? Option(string name) => Options is not null && Options.TryGetValue(name, out var v) ? v : null;
}

public enum WriteMode
{
    /// <summary>Échec si la cible existe déjà.</summary>
    CreateNew,
    Overwrite,
}

public sealed record WriteRequest(string Resource, WriteMode Mode = WriteMode.CreateNew, IReadOnlyDictionary<string, string>? Options = null, string? RunId = null)
{
    public string? Option(string name) => Options is not null && Options.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Résultat d'écriture d'un enregistrement du lot (index dans le lot).</summary>
public sealed record RecordOutcome(int Index, bool Success, string? Error = null, ErrorKind? Kind = null);

public sealed record WriteResult(IReadOnlyList<RecordOutcome> Outcomes)
{
    public int Succeeded => Outcomes.Count(o => o.Success);

    public int Failed => Outcomes.Count(o => !o.Success);

    public static WriteResult AllSucceeded(int count) => new(Enumerable.Range(0, count).Select(i => new RecordOutcome(i, true)).ToArray());
}

public interface IRecordReader : IAsyncDisposable
{
    /// <summary>Lit par lots à partir du curseur (null = début). Le curseur d'un lot permet de reprendre juste après lui.</summary>
    IAsyncEnumerable<RecordBatch> ReadAsync(string? cursor, CancellationToken ct);
}

/// <summary>
/// Lecteur qui sait marquer sa source comme traitée (courriel déplacé dans un dossier, marqué lu…).
/// Le moteur ne l'appelle qu'à la fin d'une exécution réelle sans rejet, après la publication de la cible :
/// jamais pour un aperçu, jamais après un échec. Un échec du marquage laisse les données écrites et donne un avertissement.
/// </summary>
public interface ISourceAcknowledgement
{
    Task AcknowledgeAsync(CancellationToken ct);
}

public interface IRecordWriter : IAsyncDisposable
{
    Task<WriteResult> WriteBatchAsync(IReadOnlyList<Record> batch, CancellationToken ct);

    /// <summary>Valide l'écriture (renommage atomique pour un fichier). Sans appel, rien n'est visible côté cible fichier.</summary>
    Task CompleteAsync(CancellationToken ct);
}

public interface IConnector
{
    ConnectorManifest Manifest { get; }

    Task<ConnectionTestResult> TestAsync(ConnectorSettings settings, CancellationToken ct);

    Task<DataStructure> DiscoverAsync(ConnectorSettings settings, string? resource, CancellationToken ct);

    IRecordReader CreateReader(ConnectorSettings settings, ReadRequest request);

    IRecordWriter CreateWriter(ConnectorSettings settings, WriteRequest request);
}
