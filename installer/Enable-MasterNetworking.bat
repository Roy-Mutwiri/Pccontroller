@echo off
REM One-time network setup for a PC that acts as the MASTER. Run this once on any PC you switch
REM to the Master role (it asks for administrator permission - that's expected and required).
REM
REM What it does and why: Windows only lets an app accept network connections from other PCs if
REM (a) the web-listener URLs are reserved for it ("URL ACLs") and (b) the firewall allows the
REM ports. Without this, the Master silently runs in localhost-only mode - it looks fine on its
REM own screen, but render nodes can neither discover it nor connect to it. Render Nodes do NOT
REM need this - only the Master PC does.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"%~dp0Enable-MasterNetworking.ps1\"'"
echo.
echo A new administrator window opened to apply the settings - approve the prompt and wait for
echo it to say DONE, then restart the TradeFix Master app on this PC.
pause
