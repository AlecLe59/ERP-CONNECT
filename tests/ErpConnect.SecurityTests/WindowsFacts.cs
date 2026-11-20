namespace ErpConnect.SecurityTests;

/// <summary>Test exécuté uniquement sous Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows uniquement.";
        }
    }
}

/// <summary>
/// Test exigeant le service ERPConnectEngine réellement installé (tools/dev/Install-DevService.ps1 ou MSI),
/// exécuté en administrateur avec ERPCONNECT_INSTALLED_TESTS=1.
/// </summary>
public sealed class InstalledServiceFactAttribute : FactAttribute
{
    public InstalledServiceFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows uniquement, service installé requis.";
        }
        else if (Environment.GetEnvironmentVariable("ERPCONNECT_INSTALLED_TESTS") != "1")
        {
            Skip = "Définir ERPCONNECT_INSTALLED_TESTS=1 sur une machine où le service est installé.";
        }
    }
}
