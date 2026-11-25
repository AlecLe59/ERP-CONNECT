#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installe ERP Connect Engine comme service Windows sur un poste de développement.
  Reproduit les étapes que fera le MSI (dossier de validation, section 7).
.DESCRIPTION
  ATTENTION : script écrit sans avoir pu être exécuté sur Windows lors de la Phase 2 (voir le rapport de phase).
  À valider sur Windows 11 et Windows Server 2022 avant toute utilisation chez un client.
.EXAMPLE
  .\tools\dev\Install-DevService.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $InstallDir = (Join-Path $env:ProgramFiles "ERP Connect"),
    # Binaires déjà publiés (kit de test) : pas de compilation, le SDK .NET n'est pas nécessaire.
    [string] $BinariesDir,
    # Ajoute l'utilisateur courant au groupe « ERP Connect Administrateurs » (effectif après reconnexion à Windows).
    [switch] $AddCurrentUser
)

$ErrorActionPreference = "Stop"
$ServiceName = "ERPConnectEngine"
$ServiceAccount = "NT SERVICE\$ServiceName"
$DataRoot = Join-Path $env:ProgramData "ERPConnect"
$RepoRoot = if ($BinariesDir) { $null } else { Resolve-Path (Join-Path $PSScriptRoot "..\..") }
$Groups = @(
    @{ Name = "ERP Connect Administrateurs"; Description = "Rôle Administrateur ERP Connect" },
    @{ Name = "ERP Connect Concepteurs";     Description = "Rôle Concepteur ERP Connect" },
    @{ Name = "ERP Connect Opérateurs";      Description = "Rôle Opérateur ERP Connect" },
    @{ Name = "ERP Connect Lecteurs";        Description = "Rôle Lecteur ERP Connect" },
    @{ Name = "ERP Connect Auditeurs";       Description = "Rôle Auditeur ERP Connect" }
)

if ($BinariesDir) {
    Write-Host "1. Binaires du kit : $BinariesDir"
    $Source = Resolve-Path $BinariesDir
    if (-not (Test-Path (Join-Path $Source "ErpConnect.Service.exe"))) { throw "ErpConnect.Service.exe introuvable dans $Source" }
} else {
    Write-Host "1. Publication du service ($Configuration)"
    $Source = Join-Path $RepoRoot "artifacts\service"
    dotnet publish (Join-Path $RepoRoot "src\ErpConnect.Service\ErpConnect.Service.csproj") -c $Configuration -r win-x64 --self-contained false -o $Source
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué" }
}

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "   Service existant : arrêt"
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
}

Write-Host "2. Copie des binaires dans $InstallDir (lecture seule pour tous, écriture administrateurs)"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $Source "*") -Destination $InstallDir -Recurse -Force
icacls $InstallDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" | Out-Null

Write-Host "3. Création ou mise à jour du service"
$exe = Join-Path $InstallDir "ErpConnect.Service.exe"
if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    sc.exe create $ServiceName binPath= "`"$exe`"" start= delayed-auto obj= $ServiceAccount DisplayName= "ERP Connect Engine" | Out-Null
} else {
    sc.exe config $ServiceName binPath= "`"$exe`"" start= delayed-auto obj= $ServiceAccount | Out-Null
}
sc.exe description $ServiceName "Moteur d'intégration ERP Connect (aucun port réseau ouvert)." | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/300000/restart/900000 | Out-Null
sc.exe sidtype $ServiceName restricted | Out-Null
# Privilèges conservés : emprunt d'identité (lecture du jeton du client du pipe) et traversée.
sc.exe privs $ServiceName SeChangeNotifyPrivilege/SeImpersonatePrivilege | Out-Null

Write-Host "4. Dossiers de données et ACL (héritage coupé)"
foreach ($d in @("", "data", "keys", "logs", "backup")) { New-Item -ItemType Directory -Force -Path (Join-Path $DataRoot $d) | Out-Null }
icacls $DataRoot /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "${ServiceAccount}:(OI)(CI)M" | Out-Null
icacls (Join-Path $DataRoot "keys") /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "${ServiceAccount}:(OI)(CI)M" | Out-Null

Write-Host "5. Groupes locaux"
foreach ($g in $Groups) {
    if (-not (Get-LocalGroup -Name $g.Name -ErrorAction SilentlyContinue)) {
        New-LocalGroup -Name $g.Name -Description $g.Description | Out-Null
    }
}

if ($AddCurrentUser) {
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    if (-not (Get-LocalGroupMember -Group "ERP Connect Administrateurs" -Member $me -ErrorAction SilentlyContinue)) {
        Add-LocalGroupMember -Group "ERP Connect Administrateurs" -Member $me
        Write-Host "   $me ajouté au groupe ERP Connect Administrateurs : fermez la session Windows puis rouvrez-la pour que ce droit soit pris en compte."
    }
}

Write-Host "6. Source du Journal d'événements"
if (-not [System.Diagnostics.EventLog]::SourceExists("ERP Connect")) {
    New-EventLog -LogName Application -Source "ERP Connect"
}

Write-Host "7. Démarrage"
Start-Service $ServiceName
Start-Sleep -Seconds 3
Get-Service $ServiceName | Format-Table -AutoSize
Write-Host "Journaux : $DataRoot\logs"
