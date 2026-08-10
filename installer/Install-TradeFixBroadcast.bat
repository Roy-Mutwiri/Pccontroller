@echo off
REM Double-click entry point for Install-TradeFixBroadcast.ps1. Runs through the trusted,
REM Microsoft-signed cmd.exe/powershell.exe hosts so it isn't blocked by Windows Defender
REM Application Control the way an unsigned compiled installer.exe could be.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-TradeFixBroadcast.ps1"
pause
