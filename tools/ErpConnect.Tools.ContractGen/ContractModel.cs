using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErpConnect.Contracts;

namespace ErpConnect.Tools.ContractGen;

/// <summary>Décrit le contrat de façon déterministe (instantané JSON) et génère le client TypeScript.</summary>
public static class ContractModel
{
    private static readonly NullabilityInfoContext Nullability = new();

    public static IReadOnlyList<Type> AllTypes()
    {
        var roots = RpcMethods.All.SelectMany(m => new[] { m.ParamsType, m.ResultType })
            .Concat(RpcNotifications.All.Select(n => n.PayloadType))
            .Append(typeof(RpcErrorData));
        var seen = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        foreach (var t in roots)
        {
            Collect(t, seen);
        }

        return seen.Values.ToList();
    }

    public static string Snapshot()
    {
        var root = new JsonObject
        {
            ["protocolVersion"] = Protocol.Version,
            ["pipeName"] = Protocol.PipeName,
            ["maxMessageBytes"] = Protocol.MaxMessageBytes,
            ["maxJsonDepth"] = Protocol.MaxJsonDepth,
            ["methods"] = new JsonArray(RpcMethods.All.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => (JsonNode)new JsonObject
            {
                ["name"] = m.Name,
                ["permissionMode"] = m.PermissionMode.ToString(),
                ["permission"] = m.Permission,
                ["params"] = m.ParamsType.Name,
                ["result"] = m.ResultType.Name,
            }).ToArray()),
            ["notifications"] = new JsonArray(RpcNotifications.All.OrderBy(n => n.Name, StringComparer.Ordinal).Select(n => (JsonNode)new JsonObject
            {
                ["name"] = n.Name,
                ["permission"] = n.Permission,
                ["payload"] = n.PayloadType.Name,
            }).ToArray()),
            ["errors"] = new JsonArray(typeof(RpcErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral).OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["code"] = (int)f.GetRawConstantValue()! }).ToArray()),
            ["types"] = new JsonObject(AllTypes().Select(t => new KeyValuePair<string, JsonNode?>(t.Name, new JsonArray(
                Properties(t).Select(p => (JsonNode)new JsonObject
                {
                    ["name"] = JsonNamingPolicy.CamelCase.ConvertName(p.Name),
                    ["type"] = TsType(p.PropertyType, IsNullable(p)),
                }).ToArray())))),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    public static string TypeScript()
    {
        var sb = new StringBuilder();
        sb.Append("// Fichier généré par ErpConnect.Tools.ContractGen à partir de ErpConnect.Contracts.\n");
        sb.Append("// NE PAS MODIFIER À LA MAIN : relancer `dotnet run --project tools/ErpConnect.Tools.ContractGen`.\n\n");
        sb.Append("export const PROTOCOL_VERSION = \"").Append(Protocol.Version).Append("\";\n\n");
        foreach (var t in AllTypes())
        {
            sb.Append("export interface ").Append(t.Name).Append(" {\n");
            foreach (var p in Properties(t))
            {
                sb.Append("  ").Append(JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Append(": ")
                  .Append(TsType(p.PropertyType, IsNullable(p))).Append(";\n");
            }

            sb.Append("}\n\n");
        }

        sb.Append("export interface RpcMethodMap {\n");
        foreach (var m in RpcMethods.All)
        {
            sb.Append("  \"").Append(m.Name).Append("\": { params: ").Append(m.ParamsType.Name)
              .Append("; result: ").Append(m.ResultType.Name).Append(" };\n");
        }

        sb.Append("}\n\nexport type RpcMethodName = keyof RpcMethodMap;\n\n");
        sb.Append("export interface RpcNotificationMap {\n");
        foreach (var n in RpcNotifications.All)
        {
            sb.Append("  \"").Append(n.Name).Append("\": ").Append(n.PayloadType.Name).Append(";\n");
        }

        sb.Append("}\n\nexport type RpcNotificationName = keyof RpcNotificationMap;\n\n");
        sb.Append("export const METHOD_PERMISSIONS: Record<RpcMethodName, string | null> = {\n");
        foreach (var m in RpcMethods.All)
        {
            sb.Append("  \"").Append(m.Name).Append("\": ").Append(m.Permission is null ? "null" : $"\"{m.Permission}\"").Append(",\n");
        }

        sb.Append("};\n\nexport const RPC_ERROR_CODES = {\n");
        foreach (var f in typeof(RpcErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            sb.Append("  ").Append(f.Name).Append(": ").Append((int)f.GetRawConstantValue()!).Append(",\n");
        }

        sb.Append("} as const;\n");
        return sb.ToString();
    }

    public static IEnumerable<PropertyInfo> Properties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .OrderBy(p => p.MetadataToken);

    private static bool IsNullable(PropertyInfo p) =>
        Nullable.GetUnderlyingType(p.PropertyType) is not null || Nullability.Create(p).ReadState == NullabilityState.Nullable;

    private static void Collect(Type t, SortedDictionary<string, Type> seen)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t.IsArray)
        {
            Collect(t.GetElementType()!, seen);
            return;
        }

        if (t.Namespace != typeof(Protocol).Namespace || seen.ContainsKey(t.Name))
        {
            return;
        }

        seen[t.Name] = t;
        foreach (var p in Properties(t))
        {
            Collect(p.PropertyType, seen);
        }
    }

    private static string TsType(Type t, bool nullable)
    {
        var underlying = Nullable.GetUnderlyingType(t) ?? t;
        string core;
        if (underlying.IsArray)
        {
            core = TsType(underlying.GetElementType()!, false) + "[]";
        }
        else if (underlying == typeof(string) || underlying == typeof(DateTimeOffset) || underlying == typeof(Guid))
        {
            core = "string";
        }
        else if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(double))
        {
            core = "number";
        }
        else if (underlying == typeof(bool))
        {
            core = "boolean";
        }
        else if (underlying.Namespace == typeof(Protocol).Namespace)
        {
            core = underlying.Name;
        }
        else
        {
            throw new NotSupportedException($"Type non pris en charge dans le contrat : {underlying}.");
        }

        return nullable ? core + " | null" : core;
    }
}
