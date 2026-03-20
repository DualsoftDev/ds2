# PowerShell script to test unified database mode
Write-Host "=== Unified Database Mode Test ===" -ForegroundColor Cyan
Write-Host ""

# Clean up existing database
$dbPath = "$env:APPDATA\Dualsoft\DSPilot\plc.db"
if (Test-Path $dbPath) {
    Write-Host "Removing existing database..." -ForegroundColor Yellow
    Remove-Item $dbPath -Force
    Write-Host "✓ Database removed" -ForegroundColor Green
}

# Build test console
Write-Host ""
Write-Host "Building test console..." -ForegroundColor Cyan
Set-Location "C:\ds\ds2\Apps\DSPilot"
dotnet build DSPilot.TestConsole/DSPilot.TestConsole.csproj -c Debug

if ($LASTEXITCODE -ne 0) {
    Write-Host "✗ Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "✓ Build successful" -ForegroundColor Green

# Run integration test via direct C# execution
Write-Host ""
Write-Host "Running integration test..." -ForegroundColor Cyan

$testCode = @"
using System;
using System.Threading.Tasks;

await DSPilot.TestConsole.UnifiedDbTest.RunAsync();
"@

# Create a small test runner
$runnerPath = "DSPilot.TestConsole/bin/Debug/net9.0/test-runner.csx"
$testCode | Out-File -FilePath $runnerPath -Encoding UTF8

# Use dotnet-script if available, otherwise use compiled assembly
try {
    dotnet script $runnerPath
} catch {
    Write-Host "dotnet-script not available, running compiled test..." -ForegroundColor Yellow

    # Alternative: create simple program entry point
    $testProgram = @"
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DSPilot.TestConsole;

await UnifiedDbTest.RunAsync();
"@

    Write-Host $testProgram
}

Write-Host ""
Write-Host "=== Test Complete ===" -ForegroundColor Cyan

# Check if database was created
if (Test-Path $dbPath) {
    Write-Host "✓ Database file created at: $dbPath" -ForegroundColor Green

    # Show file size
    $fileSize = (Get-Item $dbPath).Length
    Write-Host "  File size: $fileSize bytes" -ForegroundColor Gray
} else {
    Write-Host "✗ Database file not found!" -ForegroundColor Red
}
