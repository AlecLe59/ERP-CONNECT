@echo off
rem Désinstalle ERP Connect. Les données sont conservées ; pour tout effacer : Desinstaller.cmd /tout
if /i "%1"=="/tout" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Desinstaller-ERP-Connect.ps1" -RemoveData
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Desinstaller-ERP-Connect.ps1"
)
