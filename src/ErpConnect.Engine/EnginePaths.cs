namespace ErpConnect.Engine;

/// <summary>Arborescence de %ProgramData%\ERPConnect (data, keys, logs, backup).</summary>
public sealed record EnginePaths(string Root)
{
    public string Data => Path.Combine(Root, "data");

    public string Keys => Path.Combine(Root, "keys");

    public string Logs => Path.Combine(Root, "logs");

    public string Backup => Path.Combine(Root, "backup");

    public static EnginePaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ERPConnect"));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Keys);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backup);
    }
}
