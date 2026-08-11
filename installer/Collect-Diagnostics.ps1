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

# AppDomain.UnhandledException/DispatcherUnhandledException only catch MANAGED .NET exceptions.
# A crash below that level (a native access violation in a P/Invoke call, a graphics/audio driver
# fault, etc.) kills the process instantly with nothing in our own logs or crash files, but
# Windows itself records it here - "Application Error" / ".NET Runtime" entries in the Application
# event log, with a faulting module name that pinpoints what actually crashed. Reading the event
# log doesn't need elevation for a user's own session, but wrapped defensively anyway in case a
# locked-down policy blocks it.
$null = $sb.AppendLine("")
$null = $sb.AppendLine("--- Application event log: Error/Critical events, last 2 hours ---")
try {
    $since = (Get-Date).AddHours(-2)
    $events = Get-WinEvent -FilterHashtable @{ LogName = "Application"; Level = 1, 2; StartTime = $since } -ErrorAction Stop |
        Sort-Object TimeCreated |
        Select-Object -First 40

    if ($events) {
        foreach ($evt in $events) {
            $null = $sb.AppendLine("")
            $null = $sb.AppendLine("[$($evt.TimeCreated)] $($evt.ProviderName) (Id $($evt.Id))")
            $null = $sb.AppendLine($evt.Message)
        }
    }
    else {
        $null = $sb.AppendLine("(no Error/Critical events in the last 2 hours)")
    }
}
catch {
    $null = $sb.AppendLine("(couldn't read the event log: $($_.Exception.Message))")
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
