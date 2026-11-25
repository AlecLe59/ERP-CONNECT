@echo off
rem Lance l'installation du kit de test (demande les droits administrateur).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer-ERP-Connect.ps1"
