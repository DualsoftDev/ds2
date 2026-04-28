# Fetch Monaco Editor (minified) into AasxEditor.Core so the installer can ship offline.
# Idempotent: skips download if the loader.js already exists.
#
# Usage:
#   pwsh -File scripts\fetch-monaco.ps1
# or just double-click (powershell.exe fetch-monaco.ps1).

$ErrorActionPreference = 'Stop'

$version = '0.45.0'
$repoRoot = Split-Path -Parent $PSScriptRoot
$targetDir = Join-Path $repoRoot 'AasxEditor.Core\wwwroot\lib\monaco-editor'
$loaderFile = Join-Path $targetDir 'min\vs\loader.js'

if (Test-Path $loaderFile) {
    Write-Host "Monaco already present at $targetDir — skipping." -ForegroundColor Green
    exit 0
}

$tmpZip = Join-Path $env:TEMP "monaco-editor-$version.tgz"
$tmpExtract = Join-Path $env:TEMP "monaco-editor-$version-extract"

$url = "https://registry.npmjs.org/monaco-editor/-/monaco-editor-$version.tgz"
Write-Host "Downloading Monaco Editor $version from $url ..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $url -OutFile $tmpZip -UseBasicParsing

if (Test-Path $tmpExtract) { Remove-Item -Recurse -Force $tmpExtract }
New-Item -ItemType Directory -Path $tmpExtract | Out-Null

Write-Host "Extracting..." -ForegroundColor Cyan
& tar -xzf $tmpZip -C $tmpExtract
if ($LASTEXITCODE -ne 0) { throw "tar extraction failed (exit $LASTEXITCODE)" }

if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }

# npm package layout: package/min/vs, package/package.json, etc. Copy min/ only (small, sufficient).
$pkgMin = Join-Path $tmpExtract 'package\min'
if (-not (Test-Path $pkgMin)) { throw "Expected 'package/min' inside $tmpZip but not found" }
Copy-Item -Recurse -Force $pkgMin (Join-Path $targetDir 'min')

Remove-Item -Force $tmpZip
Remove-Item -Recurse -Force $tmpExtract

Write-Host "Monaco Editor $version installed to $targetDir" -ForegroundColor Green
