param([switch] $RemoveData)
$ErrorActionPreference = "Stop"
$principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $args2 = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    if ($RemoveData) { $args2 += "-RemoveData" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $args2
    exit
}
try {
    & (Join-Path $PSScriptRoot "scripts\Uninstall-DevService.ps1") -RemoveData:$RemoveData
    Write-Host "ERP Connect est désinstallé." -ForegroundColor Green
} catch {
    Write-Host "La désinstallation a échoué : $($_.Exception.Message)" -ForegroundColor Red
} finally {
    Read-Host "Appuyez sur Entrée pour fermer"
}
