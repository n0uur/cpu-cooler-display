#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs CoolerDisplay as a startup task and (optionally) removes the vendor "CPU TEMP Monitor".

.DESCRIPTION
  1. Installs the PawnIO driver via winget if missing (needed for CPU temperature/power).
  2. Copies the published build from .\publish to C:\Program Files\CoolerDisplay.
  3. Verifies that a real CPU temperature can be read. Stops here if it cannot.
  4. With -RemoveVendor: stops and uninstalls "CPU TEMP Monitor" (and its WinRing0-based helper).
  5. Registers the "CoolerDisplay" scheduled task (SYSTEM, at startup, auto-restart) and starts it.

  Build first, without elevation:  dotnet publish -c Release -o publish
#>
param(
    [switch]$Fahrenheit,
    [switch]$Load,
    [switch]$RemoveVendor
)

$ErrorActionPreference = 'Stop'
$InstallDir = Join-Path $env:ProgramFiles 'CoolerDisplay'
$LogPath = Join-Path $env:ProgramData 'CoolerDisplay\cooler.log'
$Exe = Join-Path $InstallDir 'CoolerDisplay.exe'
$TaskName = 'CoolerDisplay'
$PublishDir = Join-Path $PSScriptRoot 'publish'

function Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $PublishDir 'CoolerDisplay.exe'))) {
    throw "No build in $PublishDir. Run 'dotnet publish -c Release -o publish' first."
}

Step 'PawnIO driver'
# Same check the app uses (LibreHardwareMonitor's PawnIo.IsInstalled), so install and runtime agree.
function Test-PawnIO { (& (Join-Path $PublishDir 'CoolerDisplay.exe') sensors 2>&1 | Out-String) -match 'installed=True' }
if (Test-PawnIO) {
    Write-Host 'already installed'
} else {
    winget install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements
    if (-not (Test-PawnIO)) { throw 'PawnIO installation failed.' }
}

Step "Copy build to $InstallDir"
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $TaskName
    Get-Process -Name 'CoolerDisplay' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $PublishDir '*') -Destination $InstallDir -Recurse -Force

Step 'Verify sensors'
$output = & $Exe sensors 2>&1 | Out-String
$tempLine = ($output -split "`r?`n") | Where-Object { $_ -match '^\s+Temperature\s+Core \(Tctl/Tdie\)|^\s+Temperature\s+CPU Package' } | Select-Object -First 1
Write-Host ($output -split "`r?`n" | Where-Object { $_ -match 'Elevated|PawnIO|CPU:|Temperature|Power\s+Package|CPU Total' } | Out-String)
if (-not $tempLine -or $tempLine -match '\s(0\.0|-)\s*$') {
    throw 'CPU temperature is still unreadable with PawnIO. Vendor software left untouched; nothing scheduled.'
}

# Two programs writing the display would fight, so the vendor app is always stopped and kept from autostarting.
Step 'Stop vendor "CPU TEMP Monitor"'
$vendorTask = Get-ScheduledTask -TaskName 'CPU TEMP Monitor StartupTask' -ErrorAction SilentlyContinue
if ($vendorTask) { Disable-ScheduledTask -TaskName $vendorTask.TaskName | Out-Null }
Get-Process -Name 'DeviceDriver', 'OpenHardwareMonitorServer', 'OpenHardwareMonitorServer.vshost' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like '*CPU TEMP Monitor*' } | Stop-Process -Force

if ($RemoveVendor) {
    Step 'Uninstall vendor "CPU TEMP Monitor"'
    if ($vendorTask) { Unregister-ScheduledTask -TaskName $vendorTask.TaskName -Confirm:$false }
    $uninstaller = Join-Path ${env:ProgramFiles} 'CPU TEMP Monitor\unins000.exe'
    if (Test-Path $uninstaller) {
        Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    }
    # WinRing0 helper driver the vendor's LibreHardwareMonitor 0.9.4 registers on first run.
    foreach ($svc in 'R0OpenHardwareMonitorServer', 'WinRing0_1_2_0') {
        if (Get-Service -Name $svc -ErrorAction SilentlyContinue) {
            sc.exe stop $svc | Out-Null
            sc.exe delete $svc | Out-Null
        }
    }
    if (Test-Path (Join-Path ${env:ProgramFiles} 'CPU TEMP Monitor')) {
        Remove-Item (Join-Path ${env:ProgramFiles} 'CPU TEMP Monitor') -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Step "Register scheduled task '$TaskName'"
$arguments = @('run', '--log', "`"$LogPath`"")
if ($Fahrenheit) { $arguments += '--fahrenheit' }
if ($Load) { $arguments += '--load' }
$action = New-ScheduledTaskAction -Execute $Exe -Argument ($arguments -join ' ') -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Pushes CPU temperature and power to the cooler display (replaces CPU TEMP Monitor).' -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 3

Step 'Status'
Get-ScheduledTask -TaskName $TaskName | Select-Object TaskName, State | Format-Table -AutoSize | Out-String | Write-Host
if (Test-Path $LogPath) { Get-Content -Encoding UTF8 $LogPath -Tail 6 | Write-Host }
