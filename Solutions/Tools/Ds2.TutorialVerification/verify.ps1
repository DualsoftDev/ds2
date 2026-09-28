param(
    [ValidateSet("Replay", "Generate", "Negative", "Probes")][string]$Mode = "Replay",
    [int[]]$Ids = @(),
    [string]$Ds2SolutionsRoot = "C:\ds\ds2\Solutions"
)
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "Ds2.TutorialVerification.fsproj"
$artifacts = Join-Path $PSScriptRoot ".artifacts"
& dotnet build $project --artifacts-path $artifacts --nologo -v:minimal "/p:Ds2SolutionsRoot=$Ds2SolutionsRoot"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$dll = Join-Path $artifacts "bin\Ds2.TutorialVerification\debug\Ds2.TutorialVerification.dll"
$output = Join-Path $PSScriptRoot ("runs\" + (Get-Date -Format "yyyyMMdd-HHmmss-fff") + "-" + $Mode)
$verified = Join-Path $PSScriptRoot "verified"
if ($Mode -eq "Probes") {
    & dotnet $dll --probes $verified $output
} else {
    $runArgs = @((Join-Path $PSScriptRoot "catalog.json"), $output)
    if ($Mode -eq "Negative") { $Ids = @(22,35,52,64,95) }
    if ($Ids.Count -gt 0) { $runArgs += ($Ids -join ",") }
    if ($Mode -ne "Generate") { $runArgs += @("--replay", $verified) }
    if ($Mode -eq "Negative") { $runArgs += "--negative" }
    & dotnet $dll @runArgs
}
$code = $LASTEXITCODE
Write-Host "Results: $output"
exit $code
