#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the CoolerDisplay scheduled task and program files. PawnIO is left installed because other
  hardware tools (LibreHardwareMonitor, Fan Control, ...) share it; remove it with
  `winget uninstall namazso.PawnIO` if nothing else needs it.
#>
param([switch]$KeepLogs)

$ErrorActionPreference = 'Stop'
$TaskName = 'CoolerDisplay'

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $TaskName
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
Get-Process -Name 'CoolerDisplay' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-Item (Join-Path $env:ProgramFiles 'CoolerDisplay') -Recurse -Force -ErrorAction SilentlyContinue
if (-not $KeepLogs) { Remove-Item (Join-Path $env:ProgramData 'CoolerDisplay') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'CoolerDisplay removed.'
