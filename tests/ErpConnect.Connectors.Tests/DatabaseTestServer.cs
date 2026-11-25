using System.Text.Json;
using ErpConnect.Connectors.Sdk;

namespace ErpConnect.Connectors.Tests;

/// <summary>
/// Test exigeant un vrai serveur de base de données. La connexion est lue dans une variable d'environnement
/// (objet JSON : host, port, database, username, password…), jamais dans le dépôt. Sans elle, le test est ignoré,
/// sauf si ERPCONNECT_REQUIRE_DB=1 (CI Linux) : il échoue alors, pour qu'un serveur absent ne passe pas pour un succès.
/// </summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) is null && Environment.GetEnvironmentVariable("ERPCONNECT_REQUIRE_DB") != "1")
        {
            Skip = $"Serveur de test absent ({variable}).";
        }
    }
}

/// <summary>Lecture de la configuration d'un serveur de test depuis sa variable d'environnement (JSON).</summary>
public static class DatabaseTestServer
{
    public static (Dictionary<string, string?> Values, Dictionary<string, string> Secrets) Read(string variable)
    {
        var json = Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"Variable {variable} absente : serveur de test non configuré.");
        var values = new Dictionary<string, string?>();
        var secrets = new Dictionary<string, string>();
        foreach (var p in JsonDocument.Parse(json).RootElement.EnumerateObject())
        {
            var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
            if (p.Name == "password")
            {
                secrets["password"] = v!;
            }
            else
            {
                values[p.Name] = v;
            }
        }

        return (values, secrets);
    }

    public static ConnectorSettings Settings(string variable)
    {
        var (values, secrets) = Read(variable);
        return new ConnectorSettings(values, secrets);
    }
}
