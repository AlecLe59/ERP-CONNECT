param([string] $ForUser)
<#
  Kit de test ERP Connect : installe le moteur comme service Windows et donne à l'utilisateur le rôle Administrateur.
  Version de test, pas l'installeur final (MSI). Premier passage sur une vraie machine Windows : signaler tout message d'erreur.
#>
$ErrorActionPreference = "Stop"
$principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Relance en administrateur ; l'utilisateur ajouté au groupe reste celui qui a lancé le kit.
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    Start-Process powershell.exe -Verb RunAs -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-ForUser", "`"$me`"")
    exit
}

$log = Join-Path $PSScriptRoot "installation.log"
Start-Transcript -Path $log -Force | Out-Null
try {
    & (Join-Path $PSScriptRoot "scripts\Install-DevService.ps1") -BinariesDir (Join-Path $PSScriptRoot "service")
    if (-not $ForUser) { $ForUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name }
    if (-not (Get-LocalGroupMember -Group "ERP Connect Administrateurs" -Member $ForUser -ErrorAction SilentlyContinue)) {
        Add-LocalGroupMember -Group "ERP Connect Administrateurs" -Member $ForUser
    }
    Write-Host ""
    Write-Host "Installation terminée. $ForUser a le rôle Administrateur ERP Connect." -ForegroundColor Green
    Write-Host "IMPORTANT : fermez la session Windows puis rouvrez-la, puis lancez « ERP Connect.exe »." -ForegroundColor Yellow
} catch {
    Write-Host ""
    Write-Host "L'installation a échoué : $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Envoyez le fichier $log pour analyse."
} finally {
    Stop-Transcript | Out-Null
    Read-Host "Appuyez sur Entrée pour fermer"
}
