# Crée les groupes locaux d'ERP Connect s'ils n'existent pas. Ne supprime jamais un groupe (désinstallation comprise).
# ÉTAT : NON TESTÉ sur une machine Windows réelle.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$groups = @(
    @{ Name = 'ERP Connect Administrateurs'; Description = 'Administration complète d''ERP Connect' },
    @{ Name = 'ERP Connect Concepteurs';     Description = 'Conception des flux ERP Connect' },
    @{ Name = 'ERP Connect Opérateurs';      Description = 'Exécution et suivi des flux ERP Connect' },
    @{ Name = 'ERP Connect Lecteurs';        Description = 'Consultation ERP Connect' },
    @{ Name = 'ERP Connect Auditeurs';       Description = 'Lecture du journal d''audit ERP Connect' }
)
foreach ($g in $groups) {
    if (-not (Get-LocalGroup -Name $g.Name -ErrorAction SilentlyContinue)) {
        New-LocalGroup -Name $g.Name -Description $g.Description | Out-Null
        Write-Output "Groupe créé : $($g.Name)"
    }
}
