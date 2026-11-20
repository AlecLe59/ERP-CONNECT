#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Premier point à vérifier de la Phase 2 : DPAPI (contexte utilisateur) est-il stable pour le compte virtuel
  NT SERVICE\ERPConnectEngine, après redémarrage du service, de la machine et après mise à jour Windows ?
.DESCRIPTION
  Au démarrage, le service crée la clé maître (premier démarrage) puis la déprotège immédiatement.
  À chaque démarrage suivant, il doit relire et déprotéger la même clé. Un échec arrête le service
  avec « La clé maître ne peut pas être déprotégée par DPAPI » dans les journaux.
  Étapes : lancer ce script, puis le relancer après un redémarrage de la machine, puis après une mise à jour.
#>
$ErrorActionPreference = "Stop"
$ServiceName = "ERPConnectEngine"
$DataRoot = Join-Path $env:ProgramData "ERPConnect"
$keyFile = Join-Path $DataRoot "keys\master.v1.key"

function Show-State($label) {
    $svc = Get-Service $ServiceName
    $hash = if (Test-Path $keyFile) { (Get-FileHash $keyFile -Algorithm SHA256).Hash.Substring(0, 16) } else { "absente" }
    $errors = Select-String -Path (Join-Path $DataRoot "logs\*.log") -Pattern "DPAPI|clé maître" -ErrorAction SilentlyContinue | Select-Object -Last 3
    [pscustomobject]@{ Etape = $label; Service = $svc.Status; CleV1 = $hash; ErreursDpapi = ($errors | ForEach-Object Line) -join " | " }
}

$results = @()
$results += Show-State "Initial"
Restart-Service $ServiceName; Start-Sleep 5
$results += Show-State "Après redémarrage du service"
Stop-Process -Name "ErpConnect.Service" -Force -ErrorAction SilentlyContinue; Start-Sleep 70
$results += Show-State "Après arrêt forcé (redémarrage automatique attendu en 1 min)"
$results | Format-Table -AutoSize -Wrap
Write-Host "Résultat attendu : Service = Running à chaque étape, même empreinte de clé, aucune erreur DPAPI."
Write-Host "Relancer ce script après un redémarrage de la machine et après une mise à jour Windows."
