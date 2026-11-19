using ErpConnect.Tools.ContractGen;

// Usage : dotnet run --project tools/ErpConnect.Tools.ContractGen [-- <racine du dépôt>]
var root = args.Length > 0 ? args[0] : FindRepositoryRoot();
var ts = Path.Combine(root, "ui", "packages", "contracts", "src", "generated.ts");
var snapshot = Path.Combine(root, "src", "ErpConnect.Contracts", "contract.snapshot.json");
Directory.CreateDirectory(Path.GetDirectoryName(ts)!);
await File.WriteAllTextAsync(ts, ContractModel.TypeScript().Replace("\r\n", "\n", StringComparison.Ordinal)).ConfigureAwait(false);
await File.WriteAllTextAsync(snapshot, ContractModel.Snapshot()).ConfigureAwait(false);
Console.WriteLine($"Écrit : {ts}");
Console.WriteLine($"Écrit : {snapshot}");
return 0;

static string FindRepositoryRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ErpConnect.slnx")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? throw new InvalidOperationException("Racine du dépôt introuvable.");
}
