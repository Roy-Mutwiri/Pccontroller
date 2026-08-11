<#
.SYNOPSIS
  Gathers the Agent/Master/Launcher logs and any crash traces on this PC into one block of text,
  copies it to the clipboard, and saves a backup copy to the Desktop.

.NOTES
  Invoked via the .bat wrapper (Collect-Diagnostics.bat), same reasoning as
  Install-TradeFixBroadcast.ps1 - a script running through the trusted, signed powershell.exe/
  cmd.exe hosts is the reliable way to run something on a PC that might not run an unsigned exe.
#>

$ErrorActionPreference = "Continue"

$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine("=== TradeFix Broadcast diagnostics ($(Get-Date)) ===")
$null = $sb.AppendLine("Machine: $env:COMPUTERNAME")

foreach ($app in @("Agent", "Master", "Launcher")) {
    $logDir = Join-Path $env:LOCALAPPDATA "TradeFixBroadcast\$app\logs"
    $null = $sb.AppendLine("")
    $null = $sb.AppendLine("--- $app log ($logDir) ---")

    $latest = Get-ChildItem -Path $logDir -Filter "*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if ($latest) {
        $null = $sb.AppendLine("File: $($latest.FullName)")
        $tail = Get-Content -Path $latest.FullName -Tail 80 -ErrorAction SilentlyContinue
        $null = $sb.AppendLine(($tail -join "`n"))
    }
    else {
        $null = $sb.AppendLine("(no log file found - this app may never have run on this PC)")
    }
}

foreach ($crashFile in @("tfagent-crash.txt", "tfmaster-crash.txt", "tflauncher-crash.txt")) {
    $path = Join-Path $env:TEMP $crashFile
    $null = $sb.AppendLine("")
    $null = $sb.AppendLine("--- $crashFile ($path) ---")

    if (Test-Path $path) {
        $null = $sb.AppendLine((Get-Content -Path $path -Raw -ErrorAction SilentlyContinue))
    }
    else {
        $null = $sb.AppendLine("(not present)")
    }
}

$text = $sb.ToString()

$outFile = Join-Path ([Environment]::GetFolderPath("Desktop")) "TradeFixBroadcast-diagnostics.txt"
$text | Out-File -FilePath $outFile -Encoding utf8

try {
    $text | Set-Clipboard
    Write-Host ""
    Write-Host "Diagnostics copied to your clipboard - just paste (Ctrl+V) them now." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Couldn't copy to the clipboard automatically - that's OK, just open this file and copy its contents instead:" -ForegroundColor Yellow
}

Write-Host "A copy was also saved to: $outFile" -ForegroundColor Cyan
