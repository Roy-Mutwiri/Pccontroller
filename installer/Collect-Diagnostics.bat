@echo off
REM Double-click this on the PC having trouble. It gathers the Agent/Master/Launcher logs and any
REM crash traces into one block of text, copies it straight to your clipboard, and saves a backup
REM copy to your Desktop. No manual file-hunting - just run this, then paste (Ctrl+V) wherever you
REM need to share it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-Diagnostics.ps1"
echo.
pause
