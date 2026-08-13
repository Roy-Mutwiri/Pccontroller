<#
.SYNOPSIS
  One-time, admin-elevated network setup for a Master PC: reserves the control-server URLs
  (URL ACLs for HttpListener) and opens the firewall for the control port (TCP 8791) and the
  discovery beacon (UDP 8790). Invoked elevated by Enable-MasterNetworking.bat.

.NOTES
  Idempotent: re-running deletes and re-adds the same reservations/rules. Render Nodes never
  need this — outbound connections aren't restricted; only the LISTENING side (the Master) is.
#>

$ErrorActionPreference = "Continue"
$port = 8791
$discoveryPort = 8790

# "Everyone" is a localized account name (e.g. "Jeder" on German Windows) - resolve it from the
# well-known SID S-1-1-0 so the grant works on any display language.
$everyone = (New-Object System.Security.Principal.SecurityIdentifier("S-1-1-0")).Translate([System.Security.Principal.NTAccount]).Value

Write-Host "Reserving control-server URLs for port $port..." -ForegroundColor Cyan
foreach ($path in @("ws", "assets", "media", "audio")) {
    $url = "http://+:$port/$path/"
    netsh http delete urlacl url=$url | Out-Null
    netsh http add urlacl url=$url user="$everyone" | Out-Null
    Write-Host "  reserved $url"
}

Write-Host "Opening firewall for the Master..." -ForegroundColor Cyan
Remove-NetFirewallRule -DisplayName "TradeFix Broadcast Master (control)" -ErrorAction SilentlyContinue
Remove-NetFirewallRule -DisplayName "TradeFix Broadcast Master (discovery)" -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "TradeFix Broadcast Master (control)" -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort $port -Profile Any | Out-Null
New-NetFirewallRule -DisplayName "TradeFix Broadcast Master (discovery)" -Direction Inbound -Action Allow `
    -Protocol UDP -LocalPort $discoveryPort -Profile Any | Out-Null
Write-Host "  allowed inbound TCP $port and UDP $discoveryPort"

Write-Host ""
Write-Host "DONE. Restart the TradeFix Master app on this PC - render nodes can now find and connect to it." -ForegroundColor Green
pause
