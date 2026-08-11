<#
.SYNOPSIS
  Installs TradeFix Broadcast for the current user: copies the pre-built Master, Agent, and
  Launcher apps into place, creates Start Menu/Desktop shortcuts to the Launcher, registers an
  uninstall entry, and checks for Tailscale (needed only if render nodes aren't on the same LAN as
  the Master  -  see docs/NODE_SYSTEM.md).

.NOTES
  Per-user install (%LocalAppData%\Programs\TradeFix Broadcast)  -  deliberately no admin elevation
  required, same model as e.g. VS Code's user installer.

  This is a .ps1 invoked via the .bat wrapper (Install-TradeFixBroadcast.bat), not double-clicked
  directly  -  .ps1 files don't run on double-click by default, and PowerShell script execution can
  be blocked by policy anyway. The .bat wrapper is also what keeps this installer usable on a
  Windows Defender Application Control-restricted PC: unlike an unsigned compiled installer.exe
  (which WDAC can and did block elsewhere in this project  -  see docs/KNOWN_LIMITATIONS.md),
  PowerShell and cmd.exe are Microsoft-signed hosts that WDAC trusts, so a script-based installer
  runs everywhere a compiled one might not.
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot "publish"
$installRoot = Join-Path $env:LOCALAPPDATA "Programs\TradeFix Broadcast"

$apps = @(
    @{ Name = "Master"; Source = Join-Path $publishRoot "TradeFix.Master-win-x64"; Exe = "TradeFix.Master.exe" },
    @{ Name = "Agent"; Source = Join-Path $publishRoot "TradeFix.Agent-win-x64"; Exe = "TradeFix.Agent.exe" },
    @{ Name = "Launcher"; Source = Join-Path $publishRoot "TradeFix.Launcher-win-x64"; Exe = "TradeFix.Launcher.exe" }
)

# Everything below is transcript-logged to a file next to this script, in addition to the console.
# The console window itself is not a reliable diagnostic surface: it can be closed by the user, by
# a AV/EDR product silently terminating a "powershell -ExecutionPolicy Bypass -File ..." process
# (a pattern real-time protection commonly flags, since it's also how droppers behave), or by
# double-clicking the .bat from inside Explorer's zip-preview host rather than an extracted folder.
# The log file survives all of those, so a failure that closes the window instantly can still be
# diagnosed after the fact instead of just being "it closed, no idea why."
$logPath = Join-Path $PSScriptRoot "Install-Log.txt"
Start-Transcript -Path $logPath -Append | Out-Null

try {

Write-Host "TradeFix Broadcast  -  Setup" -ForegroundColor Cyan
Write-Host ""

# Files extracted from a zip downloaded via a browser carry Windows' Mark-of-the-Web (a hidden
# Zone.Identifier alternate data stream), which is what lets SmartScreen/App Control silently
# kill an unrecognized, unsigned exe right after launch  -  exactly the "opens and closes itself
# immediately" symptom seen with the compiled TradeFix.Setup.exe installer (see
# docs/KNOWN_LIMITATIONS.md). This script itself runs fine regardless (trusted, signed
# powershell.exe host), but the Master/Agent/Launcher exes it's about to copy into place and then
# launch are not exempt just because a script placed them. Unblock-File strips that flag from
# every file in the whole downloaded package before anything gets copied or run.
Write-Host "Unblocking downloaded files..." -ForegroundColor Cyan
Get-ChildItem -Path $repoRoot -Recurse -File -ErrorAction SilentlyContinue |
    Unblock-File -ErrorAction SilentlyContinue

foreach ($app in $apps) {
    if (-not (Test-Path (Join-Path $app.Source $app.Exe))) {
        Write-Host "Missing $($app.Source)\$($app.Exe)." -ForegroundColor Red
        Write-Host "This installer package is incomplete  -  publish\ must sit next to installer\ (run Build-Distributable.ps1, or re-download a complete package)." -ForegroundColor Red
        exit 1
    }
}

# Stop any already-running copies before overwriting their files  -  otherwise the copy below fails
# with "file in use" for a reinstall/upgrade over a currently-running install.
foreach ($processName in @("TradeFix.Master", "TradeFix.Agent", "TradeFix.Launcher")) {
    Get-Process -Name $processName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

Write-Host "Installing to $installRoot ..."
foreach ($app in $apps) {
    $destination = Join-Path $installRoot $app.Name
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -Path (Join-Path $app.Source "*") -Destination $destination -Recurse -Force
}

# Copy-Item can carry the Mark-of-the-Web over to the destination on some filesystems  -  unblock
# the installed copy too, not just the source package unblocked above.
Get-ChildItem -Path $installRoot -Recurse -File -ErrorAction SilentlyContinue |
    Unblock-File -ErrorAction SilentlyContinue

$launcherExe = Join-Path $installRoot "Launcher\TradeFix.Launcher.exe"

# Shortcuts
$shell = New-Object -ComObject WScript.Shell
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$startMenuShortcut = $shell.CreateShortcut((Join-Path $startMenuDir "TradeFix Broadcast.lnk"))
$startMenuShortcut.TargetPath = $launcherExe
$startMenuShortcut.WorkingDirectory = Split-Path -Parent $launcherExe
$startMenuShortcut.Description = "TradeFix Broadcast Control Center"
$startMenuShortcut.Save()

$desktopShortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath("Desktop")) "TradeFix Broadcast.lnk"))
$desktopShortcut.TargetPath = $launcherExe
$desktopShortcut.WorkingDirectory = Split-Path -Parent $launcherExe
$desktopShortcut.Description = "TradeFix Broadcast Control Center"
$desktopShortcut.Save()

Write-Host "Shortcuts created (Start Menu and Desktop)." -ForegroundColor Green

# Uninstall registration ("Apps & Features")
Copy-Item -Path (Join-Path $PSScriptRoot "Uninstall-TradeFixBroadcast.ps1") -Destination $installRoot -Force
Copy-Item -Path (Join-Path $PSScriptRoot "Uninstall-TradeFixBroadcast.bat") -Destination $installRoot -Force

$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TradeFixBroadcast"
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty -Path $uninstallKey -Name "DisplayName" -Value "TradeFix Broadcast Control Center"
Set-ItemProperty -Path $uninstallKey -Name "UninstallString" -Value "`"$installRoot\Uninstall-TradeFixBroadcast.bat`""
Set-ItemProperty -Path $uninstallKey -Name "Publisher" -Value "TradeFix"
Set-ItemProperty -Path $uninstallKey -Name "InstallLocation" -Value $installRoot
Set-ItemProperty -Path $uninstallKey -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name "NoRepair" -Value 1 -Type DWord

Write-Host "Registered in Apps & Features." -ForegroundColor Green

# Tailscale check  -  see docs/NODE_SYSTEM.md; only actually needed when render nodes aren't on the
# Master's LAN. Detect, don't bundle: always gets the current official build, avoids redistributing
# a third-party installer.
$tailscaleFound = (Get-Command tailscale.exe -ErrorAction SilentlyContinue) -or
    (Test-Path "${env:ProgramFiles}\Tailscale\tailscale.exe") -or
    (Test-Path "HKLM:\SOFTWARE\Tailscale IPN")

Write-Host ""
if ($tailscaleFound) {
    Write-Host "Tailscale detected  -  connecting nodes on other networks will work." -ForegroundColor Green
} else {
    Write-Host "Tailscale wasn't detected on this PC." -ForegroundColor Yellow
    Write-Host "You only need it if this PC connects to nodes that AREN'T on the same local network  - " -ForegroundColor Yellow
    Write-Host "same-LAN setups work without it. Opening the official download page now; install is optional." -ForegroundColor Yellow
    Start-Process "https://tailscale.com/download/windows"
}

Write-Host ""
Write-Host "Setup complete. Launching TradeFix Broadcast..." -ForegroundColor Cyan
Start-Process -FilePath $launcherExe -WorkingDirectory (Split-Path -Parent $launcherExe)

}
catch {
    Write-Host ""
    Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Full details were saved to: $logPath" -ForegroundColor Yellow
    throw
}
finally {
    Stop-Transcript | Out-Null
}
