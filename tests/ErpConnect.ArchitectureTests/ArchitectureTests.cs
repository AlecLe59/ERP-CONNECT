using System.Reflection;
using ErpConnect.Contracts;
using ErpConnect.Core.Authorization;

namespace ErpConnect.ArchitectureTests;

public class ArchitectureTests
{
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        ["ErpConnect.Core"] = [],
        ["ErpConnect.Contracts"] = [],
        ["ErpConnect.Security"] = ["ErpConnect.Core"],
        ["ErpConnect.Storage"] = ["ErpConnect.Core"],
        ["ErpConnect.Rpc"] = ["ErpConnect.Core", "ErpConnect.Contracts", "ErpConnect.Security"],
        ["ErpConnect.Connectors.Sdk"] = [],
        ["ErpConnect.Connectors"] = ["ErpConnect.Connectors.Sdk"],
        ["ErpConnect.Flows"] = ["ErpConnect.Connectors.Sdk"],
        ["ErpConnect.Engine"] = ["ErpConnect.Core", "ErpConnect.Storage", "ErpConnect.Security", "ErpConnect.Connectors.Sdk", "ErpConnect.Connectors", "ErpConnect.Flows"],
        ["ErpConnect.Service"] =
        [
            "ErpConnect.Core", "ErpConnect.Contracts", "ErpConnect.Security", "ErpConnect.Storage", "ErpConnect.Rpc", "ErpConnect.Engine",
            "ErpConnect.Connectors.Sdk", "ErpConnect.Connectors", "ErpConnect.Flows",
        ],
    };

    public static TheoryData<string> Projects => new(AllowedReferences.Keys);

    [Theory]
    [MemberData(nameof(Projects))]
    public void Dependencies_only_point_towards_core(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var internalRefs = assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("ErpConnect.", StringComparison.Ordinal))
            .ToList();
        internalRefs.ShouldAllBe(r => AllowedReferences[assemblyName].Contains(r), $"{assemblyName} référence : {string.Join(", ", internalRefs)}");
    }

    [Fact]
    public void No_source_project_references_the_ui_folder()
    {
        var root = RepoRoot();
        foreach (var csproj in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            File.ReadAllText(csproj).ShouldNotContain(@"..\..\ui", Case.Insensitive);
            File.ReadAllText(csproj).ShouldNotContain("../../ui", Case.Insensitive);
        }
    }

    [Fact]
    public void Every_rpc_method_declares_an_existing_permission()
    {
        foreach (var m in RpcMethods.All)
        {
            switch (m.PermissionMode)
            {
                case PermissionMode.Required:
                    m.Permission.ShouldNotBeNullOrWhiteSpace(m.Name);
                    Permissions.All.ShouldContain(m.Permission!, m.Name);
                    break;
                case PermissionMode.None:
                    m.Name.ShouldBe("session.hello", "Seule session.hello peut être sans permission.");
                    break;
                case PermissionMode.PerTopic:
                    m.Name.ShouldStartWith("events.");
                    break;
            }
        }

        foreach (var n in RpcNotifications.All)
        {
            Permissions.All.ShouldContain(n.Permission, n.Name);
        }
    }

    [Fact]
    public void No_response_type_contains_a_secret_field()
    {
        var visited = new HashSet<Type>();
        foreach (var m in RpcMethods.All)
        {
            AssertNoSecret(m.ResultType, visited, m.Name);
        }

        foreach (var n in RpcNotifications.All)
        {
            AssertNoSecret(n.PayloadType, visited, n.Name);
        }
    }

    [Fact]
    public void Secret_detection_actually_works() =>
        Should.Throw<Shouldly.ShouldAssertException>(() => AssertNoSecret(typeof(Leaky), [], "test"));

    [Fact]
    public void Every_contract_method_is_wired_in_the_service()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ErpConnect.Service", "RpcEndpoints.cs"));
        foreach (var field in typeof(RpcMethods).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(RpcMethodDescriptor)))
        {
            if (field.Name == nameof(RpcMethods.SessionHello))
            {
                continue; // géré par le répartiteur
            }

            source.ShouldContain($">(RpcMethods.{field.Name},", customMessage: $"{field.Name} n'est pas branchée dans RpcEndpoints.");
        }
    }

    [Fact]
    public void Windows_only_types_are_annotated()
    {
        foreach (var t in new[] { typeof(ErpConnect.Security.Vault.DpapiKeyProtector), typeof(ErpConnect.Rpc.Windows.WindowsPipeFactory), typeof(ErpConnect.Security.Windows.WindowsPrincipalResolver) })
        {
            t.GetCustomAttributes<System.Runtime.Versioning.SupportedOSPlatformAttribute>().ShouldContain(a => a.PlatformName == "windows");
        }
    }

    [Fact]
    public void No_test_key_protector_in_production_assemblies()
    {
        foreach (var name in AllowedReferences.Keys)
        {
            Assembly.Load(name).GetTypes().ShouldNotContain(t => t.Name.Contains("Test", StringComparison.Ordinal) && typeof(ErpConnect.Security.Vault.IKeyProtector).IsAssignableFrom(t));
        }
    }

    private static void AssertNoSecret(Type type, HashSet<Type> visited, string path)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray)
        {
            AssertNoSecret(type.GetElementType()!, visited, path);
            return;
        }

        if (type.Namespace is null || !type.Namespace.StartsWith("ErpConnect", StringComparison.Ordinal) || !visited.Add(type))
        {
            return;
        }

        foreach (var p in type.GetProperties())
        {
            var secretOnParam = type.GetConstructors().SelectMany(c => c.GetParameters())
                .Any(pa => string.Equals(pa.Name, p.Name, StringComparison.OrdinalIgnoreCase) && pa.GetCustomAttribute<SecretAttribute>() is not null);
            (p.GetCustomAttribute<SecretAttribute>() is null && !secretOnParam).ShouldBeTrue($"{path} → {type.Name}.{p.Name} est marqué secret");
            AssertNoSecret(p.PropertyType, visited, path);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ErpConnect.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private sealed record Leaky(string Name, [property: Secret] string Password);
}
