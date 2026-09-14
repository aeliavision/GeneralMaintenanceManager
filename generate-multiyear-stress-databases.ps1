param(
    [int]$StartYear = ((Get-Date).Year - 5),
    [int]$EndYear = (Get-Date).Year,
    [long]$TotalRecords = 1000000,
    [int]$WorkOrders = 100000,
    [string]$OutputDirectory = "",
    [switch]$IncludeActivity,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "tools\GeneralMaintenanceManager.ScaleHarness\GeneralMaintenanceManager.ScaleHarness.csproj"

if ($StartYear -gt $EndYear) { throw "StartYear must be <= EndYear." }
$yearCount = $EndYear - $StartYear + 1
if ($yearCount -lt 1 -or $yearCount -gt 50) { throw "Stress generation supports between 1 and 50 annual databases per run." }
if ($TotalRecords -lt $yearCount -or $TotalRecords -gt 99999999) { throw "TotalRecords must be at least the number of years and no more than 99,999,999." }
if ($WorkOrders -lt 0 -or $WorkOrders -gt 999999) { throw "WorkOrders must be between 0 and 999,999." }

function Get-YearRecordCount([int]$Year) {
    $baseCount = [long][Math]::Floor($TotalRecords / $yearCount)
    $remainder = [long]($TotalRecords % $yearCount)
    $index = $Year - $StartYear
    $extra = if ($index -lt $remainder) { [long]1 } else { [long]0 }
    return ([long]$baseCount + [long]$extra)
}

$stressRoot = [System.IO.Path]::GetFullPath((Join-Path $root "release\stress-runs"))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $stressRoot "GMM-MultiYear-$StartYear-$EndYear-$TotalRecords-total"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $root $OutputDirectory
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$prefix = $stressRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Stress fixture output must stay inside the current project under '$stressRoot'. Requested: $OutputDirectory"
}
$dataDirectory = Join-Path $OutputDirectory "Data"

if (Test-Path -LiteralPath $OutputDirectory) {
    $existing = @(Get-ChildItem -LiteralPath $OutputDirectory -Force -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0 -and -not $Force.IsPresent) {
        throw "Output directory is not empty: $OutputDirectory`nUse -Force to replace this generated fixture directory."
    }
    if ($Force.IsPresent) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$distribution = (($StartYear..$EndYear) | ForEach-Object { "$_=$((Get-YearRecordCount $_).ToString('N0'))" }) -join ', '
Write-Host "General Maintenance Manager multi-year stress fixture" -ForegroundColor Cyan
Write-Host "Years: $StartYear-$EndYear ($yearCount annual databases)"
Write-Host "Total Maintenance records: $($TotalRecords.ToString('N0'))"
Write-Host "Annual distribution: $distribution"
Write-Host "Master Work Orders: $($WorkOrders.ToString('N0'))"
Write-Host "Output Data folder: $dataDirectory"
if ($IncludeActivity.IsPresent) {
    Write-Host "Creation activity: ENABLED - one MaintenanceActivity row per Maintenance record." -ForegroundColor Yellow
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet.exe was not found on PATH." }

$argsList = @(
    "run", "--project", $project, "-c", "Release", "--",
    "--generate-fixture",
    "--start-year=$StartYear",
    "--end-year=$EndYear",
    "--total-records=$TotalRecords",
    "--work-orders=$WorkOrders",
    "--root=$dataDirectory"
)
if ($IncludeActivity.IsPresent) { $argsList += "--include-activity" }

$previousErrorActionPreference = $ErrorActionPreference
$exitCode = -1
try {
    $ErrorActionPreference = "Continue"
    & dotnet @argsList 2>&1 | ForEach-Object { $_.ToString() }
    $exitCode = $LASTEXITCODE
}
finally { $ErrorActionPreference = $previousErrorActionPreference }

if ($exitCode -ne 0) {
    throw "Multi-year stress fixture generation failed with exit code $exitCode. Generated files were preserved for diagnosis: $OutputDirectory"
}

$missing = @()
foreach ($year in $StartYear..$EndYear) {
    $path = Join-Path $dataDirectory "Years\Maintenance_$year.db"
    if (-not (Test-Path -LiteralPath $path)) { $missing += $path }
}
$master = Join-Path $dataDirectory "MaintenanceManager_Master.db"
if (-not (Test-Path -LiteralPath $master)) { $missing += $master }
if ($missing.Count -gt 0) {
    throw "Fixture generation exited successfully but expected database files are missing:`n$($missing -join [Environment]::NewLine)"
}

$tempBackups = "$dataDirectory.Backups"
if (Test-Path -LiteralPath $tempBackups) { Remove-Item -LiteralPath $tempBackups -Recurse -Force }

$readme = Join-Path $OutputDirectory "HOW_TO_USE_STRESS_FIXTURE.txt"
@"
General Maintenance Manager - Copyable Multi-Year Stress Fixture

Years: $StartYear-$EndYear
Total Maintenance records: $($TotalRecords.ToString('N0'))
Annual distribution: $distribution
Master Work Orders: $($WorkOrders.ToString('N0'))
Creation activity per record: $($IncludeActivity.IsPresent)

Generated application-ready Data folder:
$dataDirectory

The total Maintenance corpus is intentionally bounded. The standard production target is
1,000,000 Maintenance rows across six years, not 1,000,000 rows per year.

This fixture is synthetic test data only.
"@ | Set-Content -LiteralPath $readme -Encoding UTF8

Write-Host ""
Write-Host "Multi-year stress fixture: PASS" -ForegroundColor Green
Write-Host "Copyable Data folder: $dataDirectory" -ForegroundColor Green
Write-Host "Instructions: $readme" -ForegroundColor Green
