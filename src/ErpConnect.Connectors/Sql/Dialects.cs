using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;
using System.Text;
using ErpConnect.Connectors.Sdk;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace ErpConnect.Connectors.Sql;

internal static class SqlValues
{
    public static string Json(object value)
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            RecordJson.WriteValue(w, value);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Champs d'un serveur réseau : hôte, port, base, compte, chiffrement.</summary>
    public static ConfigField[] ServerFields(string defaultPort, string databaseLabel, bool databaseRequired = true) =>
    [
        new("host", "Serveur", ConfigFieldType.Text, Required: true),
        new("port", "Port", ConfigFieldType.Number, Default: defaultPort),
        new("database", databaseLabel, ConfigFieldType.Text, Required: databaseRequired),
        new("username", "Utilisateur", ConfigFieldType.Text, Required: true, Help: "Un compte en lecture seule est recommandé pour les sources."),
        new("password", "Mot de passe", ConfigFieldType.Text, Required: true, Secret: true),
        new("tls", "Chiffrement", ConfigFieldType.Choice, Default: "require", Choices: ["require", "disable"]),
        new("trustServerCertificate", "Accepter un certificat non vérifié", ConfigFieldType.Boolean, Default: "false",
            Help: "À n'activer que sur un réseau maîtrisé : le serveur n'est alors plus authentifié."),
    ];

    public static int Port(ConnectorSettings s, int fallback) => s.GetInt("port", fallback);

    public static bool Tls(ConnectorSettings s) => !string.Equals(s.Get("tls"), "disable", StringComparison.Ordinal);

    public static string Password(ConnectorSettings s) => s.GetSecret("password") ?? throw new ConnectorException(ErrorKind.Permanent, "Mot de passe manquant.");
}

public sealed class PostgreSqlDialect : SqlDialect
{
    public override string Id => "db.postgresql";

    public override string Name => "PostgreSQL";

    public override string Description => "Lecture de tables ou de requêtes SELECT (transaction en lecture seule), écriture par insertion ou mise à jour.";

    public override IReadOnlyList<ConfigField> Fields { get; } = SqlValues.ServerFields("5432", "Base");

    public override DbConnection CreateConnection(ConnectorSettings settings)
    {
        var tls = SqlValues.Tls(settings);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = settings.Require("host"),
            Port = SqlValues.Port(settings, 5432),
            Database = settings.Require("database"),
            Username = settings.Require("username"),
            Password = SqlValues.Password(settings),
            SslMode = !tls ? SslMode.Disable : settings.GetBool("trustServerCertificate") ? SslMode.Require : SslMode.VerifyFull,
            Timeout = settings.GetInt("timeoutSeconds", 30),
            ApplicationName = "ERP CONNECT",
        };
        return new NpgsqlConnection(b.ConnectionString);
    }

    public override string? ReadOnlyAfterBegin => "SET TRANSACTION READ ONLY";

    public override string TablesQuery =>
        "SELECT CASE WHEN table_schema = 'public' THEN table_name ELSE table_schema || '.' || table_name END FROM information_schema.tables " +
        "WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY 1";

    public override string Upsert(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keys)
    {
        var updates = columns.Except(keys).Select(c => $"{c} = EXCLUDED.{c}").ToList();
        return Insert(table, columns) + $" ON CONFLICT ({string.Join(", ", keys)}) " +
               (updates.Count == 0 ? "DO NOTHING" : "DO UPDATE SET " + string.Join(", ", updates));
    }

    public override object ToParameter(object? value) => value switch
    {
        DateOnly d => d,
        _ => base.ToParameter(value),
    };
}

public sealed class SqlServerDialect : SqlDialect
{
    public override string Id => "db.sqlserver";

    public override string Name => "SQL Server";

    public override string Description => "Lecture de tables ou de requêtes SELECT (transaction toujours annulée), écriture par insertion ou MERGE. Sage 100 SQL, FRP 1000 et la plupart des ERP Windows.";

    public override IReadOnlyList<ConfigField> Fields { get; } =
    [
        .. SqlValues.ServerFields("1433", "Base"),
        new("integratedSecurity", "Authentification Windows du service", ConfigFieldType.Boolean, Default: "false",
            Help: "Utilise le compte du service ERP CONNECT au lieu de l'utilisateur et du mot de passe."),
    ];

    protected override (string Open, string Close) QuoteChars => ("[", "]");

    public override bool EnforcesReadOnly => false;

    public override DbConnection CreateConnection(ConnectorSettings settings)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = settings.Require("host") + "," + SqlValues.Port(settings, 1433).ToString(CultureInfo.InvariantCulture),
            InitialCatalog = settings.Require("database"),
            Encrypt = SqlValues.Tls(settings) ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = settings.GetBool("trustServerCertificate"),
            ConnectTimeout = settings.GetInt("timeoutSeconds", 30),
            ApplicationName = "ERP CONNECT",
        };
        if (settings.GetBool("integratedSecurity"))
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = settings.Require("username");
            b.Password = SqlValues.Password(settings);
        }

        return new SqlConnection(b.ConnectionString);
    }

    public override string Savepoint => "SAVE TRANSACTION r";

    public override string RollbackToSavepoint => "ROLLBACK TRANSACTION r";

    public override string? ReleaseSavepoint => null;

    public override string TablesQuery =>
        "SELECT CASE WHEN TABLE_SCHEMA = 'dbo' THEN TABLE_NAME ELSE TABLE_SCHEMA + '.' + TABLE_NAME END FROM INFORMATION_SCHEMA.TABLES ORDER BY 1";

    public override string Upsert(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keys)
    {
        var source = string.Join(", ", columns.Select((c, i) => $"{ParameterMarker(i)} AS {c}"));
        var on = string.Join(" AND ", keys.Select(k => $"d.{k} = s.{k}"));
        var updates = columns.Except(keys).Select(c => $"d.{c} = s.{c}").ToList();
        return $"MERGE INTO {table} WITH (HOLDLOCK) AS d USING (SELECT {source}) AS s ON {on} " +
               (updates.Count == 0 ? string.Empty : $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updates)} ") +
               $"WHEN NOT MATCHED THEN INSERT ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "s." + c))});";
    }

    public override object ToParameter(object? value) => value switch
    {
        DateTimeOffset dto => dto,
        _ => base.ToParameter(value),
    };
}

public sealed class MySqlDialect : SqlDialect
{
    public override string Id => "db.mysql";

    public override string Name => "MySQL / MariaDB";

    public override string Description => "Lecture de tables ou de requêtes SELECT (transaction en lecture seule), écriture par insertion ou mise à jour.";

    public override IReadOnlyList<ConfigField> Fields { get; } = SqlValues.ServerFields("3306", "Base");

    protected override (string Open, string Close) QuoteChars => ("`", "`");

    public override DbConnection CreateConnection(ConnectorSettings settings)
    {
        var tls = SqlValues.Tls(settings);
        var b = new MySqlConnectionStringBuilder
        {
            Server = settings.Require("host"),
            Port = (uint)SqlValues.Port(settings, 3306),
            Database = settings.Require("database"),
            UserID = settings.Require("username"),
            Password = SqlValues.Password(settings),
            SslMode = !tls ? MySqlSslMode.Disabled : settings.GetBool("trustServerCertificate") ? MySqlSslMode.Required : MySqlSslMode.VerifyFull,
            ConnectionTimeout = (uint)settings.GetInt("timeoutSeconds", 30),
            ApplicationName = "ERP CONNECT",
            AllowUserVariables = false,
            AllowLoadLocalInfile = false,
        };
        return new MySqlConnection(b.ConnectionString);
    }

    public override string? ReadOnlyBeforeBegin => "SET TRANSACTION READ ONLY";

    public override string TablesQuery => "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() ORDER BY 1";

    public override string? PrimaryKeyQuery =>
        "SELECT column_name FROM information_schema.key_column_usage WHERE constraint_name = 'PRIMARY' " +
        $"AND table_name = {ParameterMarker(0)} AND table_schema = COALESCE(NULLIF({ParameterMarker(1)}, ''), DATABASE()) ORDER BY ordinal_position";

    public override string Upsert(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keys)
    {
        var updates = columns.Except(keys).Select(c => $"{c} = VALUES({c})").ToList();
        if (updates.Count == 0)
        {
            updates.Add($"{keys[0]} = {keys[0]}");
        }

        return Insert(table, columns) + " ON DUPLICATE KEY UPDATE " + string.Join(", ", updates);
    }
}

public sealed class OracleDialect : SqlDialect
{
    public override string Id => "db.oracle";

    public override string Name => "Oracle";

    public override string Description => "Lecture de tables ou de requêtes SELECT (transaction en lecture seule), écriture par insertion ou MERGE.";

    public override IReadOnlyList<ConfigField> Fields { get; } = SqlValues.ServerFields("1521", "Nom de service");

    public override string ParameterMarker(int i) => ":p" + i.ToString(CultureInfo.InvariantCulture);

    public override string ParameterName(int i) => "p" + i.ToString(CultureInfo.InvariantCulture);

    public override DbConnection CreateConnection(ConnectorSettings settings)
    {
        var protocol = SqlValues.Tls(settings) ? "TCPS" : "TCP";
        var host = settings.Require("host");
        var service = settings.Require("database");
        if (host.IndexOfAny(['(', ')', '=']) >= 0 || service.IndexOfAny(['(', ')', '=']) >= 0)
        {
            throw new ConnectorException(ErrorKind.Permanent, "Serveur ou nom de service invalide.");
        }

        var b = new OracleConnectionStringBuilder
        {
            DataSource = $"(DESCRIPTION=(ADDRESS=(PROTOCOL={protocol})(HOST={host})(PORT={SqlValues.Port(settings, 1521).ToString(CultureInfo.InvariantCulture)}))(CONNECT_DATA=(SERVICE_NAME={service})))",
            UserID = settings.Require("username"),
            Password = SqlValues.Password(settings),
            ConnectionTimeout = settings.GetInt("timeoutSeconds", 30),
        };
        return new OracleConnection(b.ConnectionString);
    }

    public override string? ReadOnlyAfterBegin => "SET TRANSACTION READ ONLY";

    public override string? ReleaseSavepoint => null;

    public override string TablesQuery => "SELECT table_name FROM user_tables UNION SELECT view_name FROM user_views ORDER BY 1";

    public override string? PrimaryKeyQuery =>
        "SELECT cc.column_name FROM all_constraints c JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name " +
        $"WHERE c.constraint_type = 'P' AND c.table_name = {ParameterMarker(0)} AND c.owner = COALESCE(NULLIF({ParameterMarker(1)}, ''), USER) ORDER BY cc.position";

    public override string Upsert(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keys)
    {
        var source = string.Join(", ", columns.Select((c, i) => $"{ParameterMarker(i)} AS {c}"));
        var on = string.Join(" AND ", keys.Select(k => $"d.{k} = s.{k}"));
        var updates = columns.Except(keys).Select(c => $"d.{c} = s.{c}").ToList();
        return $"MERGE INTO {table} d USING (SELECT {source} FROM dual) s ON ({on}) " +
               (updates.Count == 0 ? string.Empty : $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updates)} ") +
               $"WHEN NOT MATCHED THEN INSERT ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(c => "s." + c))})";
    }

    public override object ToParameter(object? value) => value switch
    {
        bool b => b ? 1L : 0L,
        _ => base.ToParameter(value),
    };

    protected override bool IsTransient(Exception ex) =>
        base.IsTransient(ex) || ex is OracleException { Number: 12541 or 12514 or 12528 or 12537 or 12170 or 3113 or 3114 or 60 or 50201 or 1466 };
}

/// <summary>
/// Tout pilote ODBC installé sur la machine (Sage, Access, Excel, bases propriétaires…). Le pilote et ses options sont
/// donnés par une chaîne de connexion ; le mot de passe est stocké à part, dans le coffre.
/// </summary>
public sealed class OdbcDialect : SqlDialect
{
    public override string Id => "db.odbc";

    public override string Name => "ODBC";

    public override string Description => "Toute base disposant d'un pilote ODBC. Lecture de tables ou de requêtes SELECT, écriture par insertion.";

    public override IReadOnlyList<ConfigField> Fields { get; } =
    [
        new("connectionString", "Chaîne de connexion ODBC", ConfigFieldType.Text, Required: true,
            Help: "Par exemple DSN=Sage ou Driver={…};Server=…;Database=…. Ne pas y mettre le mot de passe : utiliser le champ prévu."),
        new("username", "Utilisateur", ConfigFieldType.Text),
        new("password", "Mot de passe", ConfigFieldType.Text, Secret: true),
    ];

    public override bool EnforcesReadOnly => false;

    public override bool SupportsSavepoints => false;

    public override string ParameterMarker(int i) => "?";

    public override string ParameterName(int i) => "p" + i.ToString(CultureInfo.InvariantCulture);

    public override DbConnection CreateConnection(ConnectorSettings settings)
    {
        var b = new OdbcConnectionStringBuilder(settings.Require("connectionString"));
        foreach (var forbidden in new[] { "PWD", "Password" })
        {
            if (b.ContainsKey(forbidden))
            {
                throw new ConnectorException(ErrorKind.Permanent, "Le mot de passe ne doit pas figurer dans la chaîne de connexion : utiliser le champ Mot de passe.");
            }
        }

        if (settings.Get("username") is { } user)
        {
            b["UID"] = user;
        }

        if (settings.GetSecret("password") is { } pwd)
        {
            b["PWD"] = pwd;
        }

        return new OdbcConnection(b.ConnectionString);
    }

    public override string KeysetQuery(string table, string key, bool hasCursor) =>
        $"SELECT * FROM {table}{(hasCursor ? $" WHERE {key} > ?" : string.Empty)} ORDER BY {key}";

    public override string TablesQuery => string.Empty;

    public override string? PrimaryKeyQuery => null;

    public override string Upsert(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keys) =>
        throw new ConnectorException(ErrorKind.Permanent, "ODBC : la mise à jour (upsert) n'est pas disponible, seulement l'insertion.");

    /// <summary>
    /// Coupure en cours de session (08S01) ou délai dépassé (HYT00, HYT01) : transitoire. Un échec de connexion (08001) reste
    /// permanent, car les pilotes renvoient le même code pour un serveur injoignable et pour un mot de passe faux :
    /// réessayer un mot de passe faux pourrait verrouiller le compte.
    /// </summary>
    protected override bool IsTransient(Exception ex) =>
        base.IsTransient(ex) || (ex is OdbcException o && o.Errors.Count > 0 && o.Errors[0].SQLState is "08S01" or "HYT00" or "HYT01");
}
