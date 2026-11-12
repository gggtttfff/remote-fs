# Local publish helper. Same switches as .github/workflows/build.yml, so nobody
# forgets -p:PublishSingleFile=true when typing it by hand.
#
#   .\build.ps1                              publish win-x64 into artifacts\win-x64
#   .\build.ps1 -Zip                         also create remote-fs-windows-x64.zip
#   .\build.ps1 -Runtime linux-x64 -Zip      cross publish the Linux build
#   .\build.ps1 -Install                     publish straight into dist\ (close MCP clients first)
#
# NOTE: keep this file ASCII only. PowerShell 5.1 reads .ps1 as ANSI unless the
# file starts with a UTF-8 BOM, so non-ASCII text here would break parsing.

[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string]$Runtime = "win-x64",

    [string]$Output,

    [switch]$Zip,

    [switch]$Install
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$project = Join-Path $root "RemoteFs.csproj"
$executableName = if ($Runtime.StartsWith("win")) { "RemoteFs.exe" } else { "RemoteFs" }

# Artifact names match the CI matrix
$artifactNames = @{
    "win-x64"     = "remote-fs-windows-x64"
    "win-arm64"   = "remote-fs-windows-arm64"
    "linux-x64"   = "remote-fs-linux-x64"
    "linux-arm64" = "remote-fs-linux-arm64"
    "osx-x64"     = "remote-fs-macos-x64"
    "osx-arm64"   = "remote-fs-macos-arm64"
}

if (-not $Output) {
    $Output = if ($Install) { Join-Path $root "dist" } else { Join-Path $root "artifacts\$Runtime" }
}

# Before touching dist, make sure the exe is not held by a running MCP server;
# otherwise the failure shows up as an unreadable MSB error.
if ($Install) {
    $targetExe = Join-Path $Output $executableName
    if (Test-Path $targetExe) {
        try {
            $probe = [System.IO.File]::Open($targetExe, "Open", "ReadWrite", "None")
            $probe.Close()
        }
        catch {
            throw "Target $targetExe is locked. Close Claude Code / Codex (or any MCP client) and retry."
        }
    }
}

Write-Host "Publishing $Runtime -> $Output" -ForegroundColor Cyan

# Identical switches to CI: single file, self contained, compressed, no pdb
dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o $Output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit code $LASTEXITCODE)"
}

# Smoke test: the produced binary has to actually run
$publishedExe = Join-Path $Output $executableName
if ($Runtime.StartsWith("win")) {
    try {
        $version = & $publishedExe version
        Write-Host "Version check: $version" -ForegroundColor Green
    }
    catch {
        throw "Published exe failed to run: $($_.Exception.Message)"
    }
}

if ($Zip -and -not $Install) {
    $zipPath = Join-Path $root ("artifacts\" + $artifactNames[$Runtime] + ".zip")
    New-Item -ItemType Directory -Force -Path (Split-Path $zipPath) | Out-Null

    # Antivirus often keeps scanning the freshly written exe for a moment,
    # which makes Compress-Archive fail with a sharing violation. Retry.
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $zipPath -Force
            break
        }
        catch {
            if ($attempt -eq 5) {
                throw
            }
            Write-Host "File locked, retrying in 2s ($attempt/5)" -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
    }

    $size = (Get-Item $zipPath).Length / 1MB
    Write-Host ("Packed {0} ({1:N1} MB)" -f $zipPath, $size) -ForegroundColor Green
}

Write-Host "Done" -ForegroundColor Green