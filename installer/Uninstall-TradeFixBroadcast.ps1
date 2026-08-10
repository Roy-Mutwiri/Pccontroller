<#
.SYNOPSIS
  Removes TradeFix Broadcast: stops running apps, deletes the installed program files, shortcuts,
  and the Apps & Features registry entry. Deliberately leaves user data (settings, logs, paired
  node database under %LocalAppData%\TradeFixBroadcast\) in place  -  standard behavior for most
  Windows app uninstallers, and it means reinstalling later doesn't lose paired-node history.
#>

$ErrorActionPreference = "Continue" # best-effort  -  a partial uninstall is still better than none

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\TradeFix Broadcast"

# This script (and the .bat that launched it) may have inherited a working directory INSIDE
# $installRoot, e.g. if run by double-clicking Uninstall-TradeFixBroadcast.bat from within that
# folder. Windows won't delete a directory that's any live process's current directory, which
# silently broke the deferred self-delete below during testing until this was added.
Set-Location $env:TEMP

Write-Host "Uninstalling TradeFix Broadcast..." -ForegroundColor Cyan

foreach ($processName in @("TradeFix.Master", "TradeFix.Agent", "TradeFix.Launcher")) {
    Get-Process -Name $processName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

$startMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\TradeFix Broadcast.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "TradeFix Broadcast.lnk"
Remove-Item -Path $startMenuShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -Path $desktopShortcut -Force -ErrorAction SilentlyContinue

Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TradeFixBroadcast" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Removed shortcuts and Apps & Features entry." -ForegroundColor Green
Write-Host "Program files remain in use by this uninstaller  -  they'll be cleaned up now; this window will close." -ForegroundColor Cyan

# Can't delete our own running directory synchronously (this script and its .bat wrapper live
# inside $installRoot)  -  schedule the deletion via a detached cmd so it happens after this process
# exits, same trick Windows' own uninstallers use for self-deleting installers.
$cleanupCommand = "timeout /t 3 /nobreak >nul & rmdir /s /q `"$installRoot`""
Start-Process -FilePath "cmd.exe" -ArgumentList "/c", $cleanupCommand -WindowStyle Hidden

Write-Host ""
Write-Host "Your settings, logs, and paired-node history in %LocalAppData%\TradeFixBroadcast\ were kept." -ForegroundColor Yellow
Write-Host "Delete that folder too if you want a completely clean removal." -ForegroundColor Yellow
