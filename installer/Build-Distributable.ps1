<#
.SYNOPSIS
  Developer-side script: publishes self-contained builds of Master, Agent, and Launcher into
  publish\, ready to be zipped up (together with the installer\ folder) and copied to another PC.

  Not something an end user runs  -  this is the "build the distributable package" step. What end
  users actually run is Install-TradeFixBroadcast.bat, which expects the publish\ output this
  script produces to already exist alongside it.

.NOTES
  Each build is self-contained + single-file (-r win-x64 --self-contained true
  -p:PublishSingleFile=true), so the target PC needs no separate .NET runtime install  -  that's the
  "downloads/installs all requirements" part of the ask that doesn't need any actual download,
  since the runtime is embedded in each exe.
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet" # fall back to PATH on a machine where it's not at the default location
}

$targets = @(
    @{ Project = "src\TradeFix.Master\TradeFix.Master.csproj"; Output = "publish\TradeFix.Master-win-x64" },
    @{ Project = "src\TradeFix.Agent\TradeFix.Agent.csproj"; Output = "publish\TradeFix.Agent-win-x64" },
    @{ Project = "src\TradeFix.Launcher\TradeFix.Launcher.csproj"; Output = "publish\TradeFix.Launcher-win-x64" }
)

foreach ($target in $targets) {
    $projectPath = Join-Path $repoRoot $target.Project
    $outputPath = Join-Path $repoRoot $target.Output

    Write-Host "Publishing $($target.Project) -> $($target.Output)..." -ForegroundColor Cyan
    & $dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -o $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($target.Project) (exit code $LASTEXITCODE)"
    }
}

Write-Host ""
Write-Host "Done. publish\TradeFix.Master-win-x64, publish\TradeFix.Agent-win-x64, and publish\TradeFix.Launcher-win-x64 are ready." -ForegroundColor Green
Write-Host "To distribute: zip the installer\ folder together with publish\, copy to the target PC, and run Install-TradeFixBroadcast.bat." -ForegroundColor Green
