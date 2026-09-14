param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "GeneralMaintenanceManager.slnx"

Push-Location $root
try {
    & .\verify-maintenance-first.ps1 -AllowGeneratedArtifacts

    Write-Host "Using .NET SDK:" -ForegroundColor Cyan
    $sdk = (dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Unable to execute dotnet." }
    Write-Host $sdk
    if ([Version]$sdk -lt [Version]"10.0.0") { throw ".NET SDK 10 or newer is required. Found $sdk." }

    dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw "Solution restore failed." }

    dotnet build $solution -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed." }

    if (-not $SkipTests.IsPresent) {
        dotnet test $solution -c $Configuration --no-build --logger "console;verbosity=normal"
        if ($LASTEXITCODE -ne 0) { throw "Automated tests failed." }
    }

    Write-Host "Normal build gate completed successfully." -ForegroundColor Green
    if ($SkipTests.IsPresent) { Write-Host "Tests were skipped by explicit request." -ForegroundColor Yellow }
    Write-Host "Million-row scale testing is optional and separate: .\run-scale-harness.ps1 -Million" -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
