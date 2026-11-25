#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Crée les comptes locaux de test des tests de sécurité S1 à S3 et exporte les variables d'environnement.
  Mots de passe aléatoires, jamais écrits sur disque. Supprimer avec -Remove.
#>
[CmdletBinding()]
param([switch] $Remove)

$ErrorActionPreference = "Stop"
$accounts = @{ "erpc-norole" = $null; "erpc-operator" = "ERP Connect Opérateurs"; "erpc-reader" = "ERP Connect Lecteurs" }

if ($Remove) {
    foreach ($name in $accounts.Keys) { Remove-LocalUser -Name $name -ErrorAction SilentlyContinue }
    return
}

foreach ($name in $accounts.Keys) {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $plain = [Convert]::ToBase64String($bytes) + "aA1!"
    $secure = ConvertTo-SecureString $plain -AsPlainText -Force
    if (Get-LocalUser -Name $name -ErrorAction SilentlyContinue) { Set-LocalUser -Name $name -Password $secure }
    else { New-LocalUser -Name $name -Password $secure -PasswordNeverExpires -AccountNeverExpires | Out-Null }
    if ($accounts[$name]) { Add-LocalGroupMember -Group $accounts[$name] -Member $name -ErrorAction SilentlyContinue }
    if ($name -eq "erpc-norole") {
        $env:ERPCONNECT_TEST_NOROLE_USER = "$env:COMPUTERNAME\$name"
        $env:ERPCONNECT_TEST_NOROLE_PASSWORD = $plain
    }
}
$env:ERPCONNECT_INSTALLED_TESTS = "1"
Write-Host "Comptes créés. Variables définies pour cette session PowerShell uniquement."
Write-Host "Lancer : dotnet test tests\ErpConnect.SecurityTests -c Release"
