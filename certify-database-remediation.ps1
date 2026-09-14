param(
    [int]$StartYear = ((Get-Date).Year - 5),
    [int]$EndYear = (Get-Date).Year,
    [long]$TotalRecords = 1000000,
    [int]$WorkOrders = 100000,
    [string]$DataPath = "",
    [switch]$SkipActivityHeavy,
    [switch]$SkipBackupRestore
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$certificationDirectory = Join-Path $root "release\certification"
$report = Join-Path $certificationDirectory "DB10_DATABASE_CERTIFICATION_RESULTS.txt"
New-Item -ItemType Directory -Force -Path $certificationDirectory | Out-Null

if ($StartYear -gt $EndYear) { throw "StartYear must be <= EndYear." }
if (($EndYear - $StartYear + 1) -ne 6) { throw "DB-10 production certification is defined as a six-year corpus." }
if ($TotalRecords -ne 1000000) { throw "DB-10 production certification is fixed at exactly 1,000,000 Maintenance rows total across six years." }
if ($WorkOrders -lt 1 -or $WorkOrders -gt 999999) { throw "WorkOrders must be between 1 and 999,999." }
$stressRoot = [System.IO.Path]::GetFullPath((Join-Path $root "release\stress-runs"))
if ([string]::IsNullOrWhiteSpace($DataPath)) {
    $DataPath = Join-Path $stressRoot "DB10-Certification\Data"
}
elseif (-not [System.IO.Path]::IsPathRooted($DataPath)) {
    $DataPath = Join-Path $root $DataPath
}
$DataPath = [System.IO.Path]::GetFullPath($DataPath)
$prefix = $stressRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $DataPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "DB-10 stress DataPath must stay inside the current project under '$stressRoot'. Requested: $DataPath"
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("General Maintenance Manager - DB-10 Database Remediation Certification")
$lines.Add("Run: " + (Get-Date).ToString("u"))
$lines.Add("Years: $StartYear-$EndYear")
$lines.Add("Maintenance rows total: $($TotalRecords.ToString('N0'))")
$lines.Add("Activity-heavy variant: $(-not $SkipActivityHeavy.IsPresent)")
$lines.Add("Work Orders: $($WorkOrders.ToString('N0'))")
$lines.Add("")

try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet.exe was not found on PATH." }

    & (Join-Path $root "verify-maintenance-first.ps1") -AllowGeneratedArtifacts
    $lines.Add("Static architecture verifier: PASS")

    $stressArgs = @{
        TotalRecords = $TotalRecords
        WorkOrders = $WorkOrders
        FromYear = $StartYear
        ToYear = $EndYear
        DataPath = $DataPath
        Reset = $true
    }
    if (-not $SkipActivityHeavy.IsPresent) { $stressArgs.IncludeActivity = $true }
    if (-not $SkipBackupRestore.IsPresent) { $stressArgs.IncludeBackup = $true }

    & (Join-Path $root "stress-test.ps1") @stressArgs

    $stressReport = Join-Path (Split-Path -Parent $DataPath) "StressReport.txt"
    if (-not (Test-Path -LiteralPath $stressReport)) { throw "DB-10 stress evidence report was not created: $stressReport" }
    $stressText = Get-Content -LiteralPath $stressReport -Raw
    if ($stressText -notmatch [regex]::Escape("Total Maintenance records: 1000000") -and
        $stressText -notmatch [regex]::Escape("Total Maintenance records: 1,000,000")) {
        throw "DB-10 stress evidence does not report the required 1,000,000 total Maintenance rows."
    }
    if ($stressText -notmatch "Production stress test: PASS") {
        throw "DB-10 stress evidence does not contain the production stress PASS marker."
    }

    $dbFiles = @(Get-ChildItem -LiteralPath $DataPath -Recurse -File -Filter *.db)
    $expectedDbFiles = ($EndYear - $StartYear + 1) + 1
    if ($dbFiles.Count -ne $expectedDbFiles) { throw "Expected $expectedDbFiles SQLite database files, found $($dbFiles.Count)." }
    $totalDbBytes = ($dbFiles | Measure-Object Length -Sum).Sum

    $lines.Add("Six-year 1,000,000-total Maintenance corpus: PASS")
    if (-not $SkipActivityHeavy.IsPresent) {
        $lines.Add("Activity-heavy companion rows: PASS")
    } else {
        $lines.Add("Activity-heavy companion rows: SKIPPED BY EXPLICIT OPTION")
    }
    if (-not $SkipBackupRestore.IsPresent) {
        $lines.Add("Backup/restore stress: PASS")
    } else {
        $lines.Add("Backup/restore stress: SKIPPED BY EXPLICIT OPTION")
    }
    $lines.Add("SQLite files: $($dbFiles.Count)")
    $lines.Add("SQLite bytes: $totalDbBytes")
    $lines.Add("Stress evidence SHA-256: $((Get-FileHash -LiteralPath $stressReport -Algorithm SHA256).Hash.ToLowerInvariant())")
    $lines.Add("Stress Data path: $DataPath")
    $lines.Add("")
    $lines.Add("RESULT: DB-10 DATABASE CERTIFICATION PASS")
    $lines | Set-Content -LiteralPath $report -Encoding UTF8
    Write-Host "DB-10 database remediation certification: PASS" -ForegroundColor Green
    Write-Host "Report: $report" -ForegroundColor Green
    Write-Host "Stress Data: $DataPath" -ForegroundColor Green
}
catch {
    $lines.Add("")
    $lines.Add("RESULT: FAIL")
    $lines.Add($_.Exception.ToString())
    $lines | Set-Content -LiteralPath $report -Encoding UTF8
    throw
}
