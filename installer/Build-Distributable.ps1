<#
.SYNOPSIS
  Developer-side script: publishes self-contained builds of Master, Agent, Launcher, and Setup,
  then assembles dist\ into the layout end users actually get: TradeFix.Setup.exe sitting directly
  next to a publish\ folder. Not something an end user runs - this is the "build the distributable
  package" step.

.NOTES
  Each build is self-contained + single-file (-r win-x64 --self-contained true
  -p:PublishSingleFile=true), so the target PC needs no separate .NET runtime install - that's the
  "downloads/installs all requirements" part of the ask that doesn't need any actual download,
  since the runtime is embedded in each exe.

  TradeFix.Setup.exe (the compiled installer, see src\TradeFix.Setup) is the primary way to
  install: a real double-clickable .exe. The PowerShell-based Install-TradeFixBroadcast.bat in this
  same folder still works too and is kept as a documented fallback - see KNOWN_LIMITATIONS.md's
  WDAC section for why a script-based installer is occasionally the more reliable option on a
  locked-down PC.
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
    @{ Project = "src\TradeFix.Launcher\TradeFix.Launcher.csproj"; Output = "publish\TradeFix.Launcher-win-x64" },
    @{ Project = "src\TradeFix.Setup\TradeFix.Setup.csproj"; Output = "publish\TradeFix.Setup-win-x64" }
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

# Assemble dist\ into the layout TradeFix.Setup.exe itself expects at runtime
# (Installer.ResolvePublishRoot: a "publish" folder as a direct child of the exe's own directory) -
# this is what actually gets zipped and handed to another PC.
$distRoot = Join-Path $repoRoot "dist"
if (Test-Path $distRoot) {
    Remove-Item $distRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

Write-Host "Assembling dist\..." -ForegroundColor Cyan
Copy-Item (Join-Path $repoRoot "publish\TradeFix.Setup-win-x64\TradeFix.Setup.exe") (Join-Path $distRoot "TradeFix.Setup.exe")

$distPublish = Join-Path $distRoot "publish"
New-Item -ItemType Directory -Force -Path $distPublish | Out-Null
foreach ($folderName in @("TradeFix.Master-win-x64", "TradeFix.Agent-win-x64", "TradeFix.Launcher-win-x64")) {
    Copy-Item (Join-Path $repoRoot "publish\$folderName") (Join-Path $distPublish $folderName) -Recurse
}

Write-Host ""
Write-Host "Done. dist\TradeFix.Setup.exe is ready to double-click, or zip the whole dist\ folder to copy to another PC." -ForegroundColor Green
Write-Host "(installer\Install-TradeFixBroadcast.bat also still works as a script-based fallback - see installer\ for that path.)" -ForegroundColor Green
