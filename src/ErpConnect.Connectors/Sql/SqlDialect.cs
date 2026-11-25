using System.Data.Common;
using System.Globalization;
using System.Net.Sockets;
using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors.Sql;

/// <summary>
/// Ce qui distingue un moteur de base de données d'un autre : connexion, échappement des noms, paramètres,
/// lecture seule, insertion ou mise à jour, points de sauvegarde, découverte. Le reste (lecture par lots,
/// reprise, rejets ligne par ligne) est commun et se trouve dans <see cref="SqlConnector"/>.
/// </summary>
public abstract class SqlDialect
{
    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>Champs de configuration propres au moteur (hôte, port, base…). Les champs communs sont ajoutés par le connecteur.</summary>
    public abstract IReadOnlyList<ConfigField> Fields { get; }

    public abstract DbConnection CreateConnection(ConnectorSettings settings);

    /// <summary>Caractères d'échappement des identifiants.</summary>
    protected virtual (string Open, string Close) QuoteChars => ("\"", "\"");

    /// <summary>Nom du paramètre n° i dans le texte SQL.</summary>
    public virtual string ParameterMarker(int i) => "@p" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>Nom du paramètre n° i côté ADO.NET (sans préfixe pour les paramètres positionnels).</summary>
    public virtual string ParameterName(int i) => "@p" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>Ordres envoyés juste avant / juste après l'ouverture de la transaction de lecture.</summary>
    public virtual string? ReadOnlyBeforeBegin => null;

    public virtual string? ReadOnlyAfterBegin => null;

    /// <summary>Faux si le moteur ne garantit pas la lecture seule par lui-même (seule la transaction annulée protège).</summary>
    public virtual bool EnforcesReadOnly => true;

    public virtual bool SupportsSavepoints => true;

    public virtual string Savepoint => "SAVEPOINT r";

    public virtual string RollbackToSavepoint => "ROLLBACK TO SAVEPOINT r";

    public virtual string? ReleaseSavepoint => "RELEASE SAVEPOINT r";

    /// <summary>Lecture d'une table triée par sa clé, à partir d'une valeur de clé (keyset).</summary>
    public virtual string KeysetQuery(string table, string key, bool hasCursor) =>
        $"SELECT * FROM {table}{(hasCursor ? $" WHERE {key} > {ParameterMarker(0)}" : string.Empty)} ORDER BY {key}";

    /// <summary>Requête vide qui donne seulement les colonnes d'une table.</summary>
    public virtual string ColumnsQuery(string table) => $"SELECT * FROM {table} WHERE 1 = 0";

    public abstract string TablesQuery { get; }

    /// <summary>Colonnes de la clé primaire ; paramètre 0 = nom de table (sans schéma), paramètre 1 = schéma ou chaîne vide.</summary>
    public virtual string? PrimaryKeyQuery =>
        "SELECT k.column_name FROM information_schema.table_constraints t JOIN information_schema.key_column_usage k " +
        "ON k.constraint_name = t.constraint_name AND k.table_schema = t.table_schema AND k.table_name = t.table_name " +
        $"WHERE t.constraint_type = 'PRIMARY KEY' AND t.table_name = {ParameterMarker(0)} AND ({ParameterMarker(1)} = '' OR t.table_schema = {ParameterMarker(1)}) ORDER BY k.ordinal_position";

    public virtual string Insert(string table, IReadOnlyList<string> columns) =>
        $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => ParameterMarker(i)))})";

    /// <summary>Insertion ou mise à jour selon la clé. Les colonnes sont déjà échappées ; keys ⊂ columns.</summary>
    public abstract string Upsert(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keys);

    public string QuoteName(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 128 || identifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ConnectorException(ErrorKind.Permanent, "Nom de table ou de colonne invalide.");
        }

        var (open, close) = QuoteChars;
        return open + identifier.Replace(close, close + close, StringComparison.Ordinal) + close;
    }

    /// <summary>Nom éventuellement qualifié par un schéma (dbo.clients) : chaque partie est échappée.</summary>
    public string QuoteQualified(string name)
    {
        var parts = name.Trim().Split('.');
        if (parts.Length > 3)
        {
            throw new ConnectorException(ErrorKind.Permanent, "Nom de table invalide.");
        }

        return string.Join(".", parts.Select(QuoteName));
    }

    /// <summary>Valeur d'un enregistrement convertie pour le pilote.</summary>
    public virtual object ToParameter(object? value) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset dto => dto.UtcDateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        Record or IReadOnlyList<object?> => SqlValues.Json(value),
        _ => value,
    };

    /// <summary>Erreur classée : transitoire si réessayer a un sens (réseau, délai, interblocage, serveur indisponible).</summary>
    public ConnectorException Map(Exception ex, string context)
    {
        var transient = IsTransient(ex);
        return new ConnectorException(transient ? ErrorKind.Transient : ErrorKind.Permanent, $"{Name} : {context}{Message(ex)}", ex);
    }

    protected virtual bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException { IsTransient: true } or TimeoutException or SocketException)
            {
                return true;
            }
        }

        return false;
    }

    private static string Message(Exception ex) => ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
}
