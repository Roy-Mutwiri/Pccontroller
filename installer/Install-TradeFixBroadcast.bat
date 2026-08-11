@echo off
REM Double-click entry point for Install-TradeFixBroadcast.ps1. Runs through the trusted,
REM Microsoft-signed cmd.exe/powershell.exe hosts so it isn't blocked by Windows Defender
REM Application Control the way an unsigned compiled installer.exe could be.
REM
REM Writes a marker line before handing off to PowerShell, which then appends its own transcript
REM to the same file. If this window closes before you can read it, Install-Log.txt (next to this
REM .bat) tells you how far it got: nothing past this line means cmd.exe itself never got to run
REM powershell.exe (e.g. blocked before launch); a line but no further transcript output means
REM powershell.exe started then was terminated before doing anything (commonly antivirus/EDR
REM reacting to the "powershell -ExecutionPolicy Bypass -File" pattern, which is also how many
REM droppers behave, so it's a frequently-flagged combination even though this script is benign).
echo TradeFix Broadcast installer starting via cmd.exe (%DATE% %TIME%) > "%~dp0Install-Log.txt"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-TradeFixBroadcast.ps1"
echo.
echo Full log saved to: %~dp0Install-Log.txt
pause
