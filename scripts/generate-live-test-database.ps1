param(
    [string]$OutputDirectory = ".\release\stress-runs\GMM-Live-Test",
    [int]$Year = (Get-Date).Year,
    [int]$WorkOrders = 100000,
    [switch]$Reset
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$output = [System.IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$stressRoot = [System.IO.Path]::GetFullPath((Join-Path $root "release\stress-runs"))
$prefix = $stressRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Live-test stress data must stay inside the current project under '$stressRoot'. Requested: $output"
}
$data = Join-Path $output "Data"
$marker = Join-Path $data ".gmm-stress-run"

if (Test-Path -LiteralPath $data) {
    $entries = @(Get-ChildItem -LiteralPath $data -Force -ErrorAction SilentlyContinue)
    if ($entries.Count -gt 0 -and -not (Test-Path -LiteralPath $marker)) {
        throw "Refusing to use unmarked Data directory: $data`nChoose a fresh OutputDirectory."
    }
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

& (Join-Path $scriptDir "run-million-stress-test.ps1") `
    -Year $Year `
    -DataRoot $data `
    -WorkOrders $WorkOrders `
    -GenerateOnly `
    -KeepData `
    -Reset:$Reset.IsPresent
if ($LASTEXITCODE -ne 0) { throw "Persistent live-test database generation failed." }

$readme = Join-Path $output "HOW_TO_USE_LIVE_TEST_DATA.txt"
@"
General Maintenance Manager - Persistent Live Test Data

Data folder:
$data

Contains a real application-ready master database and a real annual database with:
- 1,000,000 Maintenance records for $Year
- $($WorkOrders.ToString('N0')) Work Orders
- production schema, indexes, triggers and FTS infrastructure

LIVE APP TEST
1. Publish a disposable x64 application copy with .\\publish-win-x64.ps1.
2. Close GMM.
3. Install this Data folder with:
   .\\install-live-test-database.ps1 -SourceDataDirectory `"$data`" -TargetDataDirectory `".\\release\\win-x64\\Data`"
4. Launch .\\release\\win-x64\\GeneralMaintenanceManager.exe

REUSE FOR BENCHMARK
.\\run-million-stress-test.ps1 -DataRoot `"$data`" -BenchmarkOnly -KeepData -WorkOrders $WorkOrders

The .gmm-stress-run marker protects this generated corpus from accidental reset operations.
Synthetic test data only. Never use it as production data.
"@ | Set-Content -LiteralPath $readme -Encoding UTF8
Write-Host "Persistent live-test Data folder: $data" -ForegroundColor Green
Write-Host "Instructions: $readme" -ForegroundColor Green
