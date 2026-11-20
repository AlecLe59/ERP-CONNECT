#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Désinstalle le service de développement. Les données de %ProgramData%\ERPConnect sont conservées,
  sauf avec -RemoveData.
.DESCRIPTION
  Script écrit sans avoir pu être exécuté sur Windows lors de la Phase 2.
#>
[CmdletBinding()]
param(
    [switch] $RemoveData,
    [string] $InstallDir = (Join-Path $env:ProgramFiles "ERP Connect")
)

$ErrorActionPreference = "Stop"
$ServiceName = "ERPConnectEngine"

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
}
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
foreach ($g in "ERP Connect Administrateurs", "ERP Connect Concepteurs", "ERP Connect Opérateurs", "ERP Connect Lecteurs", "ERP Connect Auditeurs") {
    Remove-LocalGroup -Name $g -ErrorAction SilentlyContinue
}
if ($RemoveData) {
    Remove-Item (Join-Path $env:ProgramData "ERPConnect") -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Données supprimées."
} else {
    Write-Host "Données conservées dans $env:ProgramData\ERPConnect."
}
