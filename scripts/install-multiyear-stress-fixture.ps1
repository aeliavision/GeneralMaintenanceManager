param(
    [Parameter(Mandatory = $true)]
    [string]$TargetDataDirectory,
    [string]$FixtureDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($FixtureDirectory)) {
    $candidate = Get-ChildItem -LiteralPath (Join-Path $root "release") -Directory -Filter "stress-fixture-*" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw "No generated stress fixture was found. Run .\generate-multiyear-stress-databases.ps1 first or pass -FixtureDirectory."
    }
    $FixtureDirectory = $candidate.FullName
}

$fixtureData = Join-Path ([System.IO.Path]::GetFullPath($FixtureDirectory)) "Data"
$targetData = [System.IO.Path]::GetFullPath($TargetDataDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $fixtureData "MaintenanceManager_Master.db"))) {
    throw "Fixture master database not found under: $fixtureData"
}
if (-not (Test-Path -LiteralPath (Join-Path $fixtureData "Years"))) {
    throw "Fixture Years directory not found under: $fixtureData"
}

$running = Get-Process -Name "GeneralMaintenanceManager" -ErrorAction SilentlyContinue
if ($null -ne $running) {
    throw "GeneralMaintenanceManager is running. Close the app before replacing its Data folder."
}

$parent = Split-Path -Parent $targetData
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$backup = $null
if (Test-Path -LiteralPath $targetData) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backup = "$targetData.before-stress-$stamp"
    Move-Item -LiteralPath $targetData -Destination $backup
    Write-Host "Existing Data folder moved to: $backup" -ForegroundColor Yellow
}

try {
    Copy-Item -LiteralPath $fixtureData -Destination $targetData -Recurse -Force
}
catch {
    if ((-not (Test-Path -LiteralPath $targetData)) -and $null -ne $backup -and (Test-Path -LiteralPath $backup)) {
        Move-Item -LiteralPath $backup -Destination $targetData
    }
    throw
}

Write-Host "Stress fixture installed: $targetData" -ForegroundColor Green
if ($null -ne $backup) { Write-Host "Original Data backup: $backup" -ForegroundColor Green }
