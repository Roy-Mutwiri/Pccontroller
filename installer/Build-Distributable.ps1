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

# Stage ffmpeg.exe next to Master and Agent - the H.264 video pipeline (see
# src\TradeFix.Sources\Video) probes for it there at runtime and silently falls back to the old
# JPEG-per-frame pipeline when it's absent, so a missing ffmpeg produces a working-but-lower-quality
# build rather than a broken one. Warn loudly either way: shipping without it forfeits the quality
# fix on every PC that installs this package.
$ffmpegSource = $null
$wingetRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
if (Test-Path $wingetRoot) {
    $ffmpegSource = Get-ChildItem $wingetRoot -Recurse -Filter "ffmpeg.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $ffmpegSource) {
    $onPath = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($onPath) { $ffmpegSource = $onPath.Source }
}

if ($ffmpegSource) {
    Write-Host "Staging ffmpeg.exe (H.264 video pipeline) from $ffmpegSource..." -ForegroundColor Cyan
    foreach ($appFolder in @("TradeFix.Master-win-x64", "TradeFix.Agent-win-x64")) {
        Copy-Item $ffmpegSource (Join-Path $repoRoot "publish\$appFolder\ffmpeg.exe") -Force
    }
}
else {
    Write-Host "WARNING: ffmpeg.exe not found on this build machine (winget install Gyan.FFmpeg to get it)." -ForegroundColor Yellow
    Write-Host "The package will still work but every PC will run the lower-quality JPEG video fallback." -ForegroundColor Yellow
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
