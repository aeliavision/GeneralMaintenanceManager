param(
    [int]$Year = (Get-Date).Year,
    [switch]$KeepData,
    [string]$DataRoot = "",
    [int]$Lookups = 200,
    [int]$Workers = 8,
    [int]$QueriesPerWorker = 25,
    [int]$WorkOrders = 100000,
    [int]$MaxWorkingSetMb = 1024,
    [switch]$SkipBackupRestore,
    [switch]$GenerateOnly,
    [switch]$BenchmarkOnly,
    [switch]$Reset
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$project = Join-Path $root "tools\GeneralMaintenanceManager.ScaleHarness\GeneralMaintenanceManager.ScaleHarness.csproj"
$reportDirectory = Join-Path $root "release\certification"
$report = Join-Path $reportDirectory "MILLION_RECORD_STRESS_RESULTS.txt"
$stressRoot = [System.IO.Path]::GetFullPath((Join-Path $root "release\stress-runs"))
if ($WorkOrders -lt 0 -or $WorkOrders -gt 999999) { throw "WorkOrders must be between 0 and 999,999." }
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null

$argsList = @(
    "run", "--project", $project, "-c", "Release", "--",
    "--stress",
    "--year=$Year",
    "--lookups=$Lookups",
    "--workers=$Workers",
    "--queries-per-worker=$QueriesPerWorker",
    "--work-orders=$WorkOrders",
    "--max-working-set-mb=$MaxWorkingSetMb"
)
if ($KeepData.IsPresent) { $argsList += "--keep" }
if ($SkipBackupRestore.IsPresent) { $argsList += "--skip-backup-restore" }
if ($GenerateOnly.IsPresent) { $argsList += "--generate-only" }
if ($BenchmarkOnly.IsPresent) { $argsList += "--benchmark-only" }
if ($Reset.IsPresent) { $argsList += "--reset" }
if (($GenerateOnly.IsPresent -or $BenchmarkOnly.IsPresent -or $Reset.IsPresent) -and [string]::IsNullOrWhiteSpace($DataRoot)) {
    throw "GenerateOnly, BenchmarkOnly, and Reset require -DataRoot."
}
if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
    $resolvedDataRoot = if ([System.IO.Path]::IsPathRooted($DataRoot)) { [System.IO.Path]::GetFullPath($DataRoot) } else { [System.IO.Path]::GetFullPath((Join-Path $root $DataRoot)) }
    $prefix = $stressRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (($GenerateOnly.IsPresent -or $BenchmarkOnly.IsPresent -or $Reset.IsPresent) -and -not $resolvedDataRoot.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Persistent stress DataRoot must stay inside the current project under '$stressRoot'. Requested: $resolvedDataRoot"
    }
    $DataRoot = $resolvedDataRoot
    $argsList += "--root=$DataRoot"
}

$processorName = "unknown"
$storage = "unknown"
try {
    $os = Get-CimInstance Win32_OperatingSystem
    $computer = Get-CimInstance Win32_ComputerSystem
    $processorName = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
    $windows = "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
    $ramGb = [Math]::Round($computer.TotalPhysicalMemory / 1GB, 1)
    try {
        $storage = ((Get-PhysicalDisk | Select-Object FriendlyName, MediaType, BusType | ForEach-Object {
            "$($_.FriendlyName) [$($_.MediaType)/$($_.BusType)]"
        }) -join "; ")
        if ([string]::IsNullOrWhiteSpace($storage)) { $storage = "unknown" }
    } catch { $storage = "unknown" }
}
catch {
    $windows = [System.Environment]::OSVersion.VersionString
    $ramGb = "unknown"
}

$dotnetVersion = if (Get-Command dotnet -ErrorAction SilentlyContinue) { (& dotnet --version).Trim() } else { "not found" }
$header = @(
    "General Maintenance Manager - 1,000,000 Record Stress Test",
    "Run: $((Get-Date).ToString('u'))",
    "Windows: $windows",
    "Machine: $([System.Environment]::MachineName)",
    "CPU: $processorName",
    "Processor count: $([System.Environment]::ProcessorCount)",
    "RAM GB: $ramGb",
    "Storage: $storage",
    ".NET SDK: $dotnetVersion",
    "Work Orders: $WorkOrders",
    "Backup/restore stress: $(-not $SkipBackupRestore.IsPresent)",
    "Command: dotnet $($argsList -join ' ')",
    ""
)
$header | Set-Content -Path $report -Encoding UTF8

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet.exe was not found on PATH."
}

# Windows PowerShell can surface native stderr as NativeCommandError when
# $ErrorActionPreference is Stop. Stream native output line-by-line so long
# stress phases stay visibly active, while also retaining every line for the
# PASS-marker check and certification report.
Write-Host ""
$modeLabel = if ($GenerateOnly.IsPresent) { "persistent corpus generation" } elseif ($BenchmarkOnly.IsPresent) { "persistent corpus benchmark" } else { "1,000,000-record stress test" }
Write-Host "Starting $modeLabel..." -ForegroundColor Cyan
Write-Host "Live phase/progress output will appear below and is also written to:" -ForegroundColor DarkGray
Write-Host "  $report" -ForegroundColor DarkGray
Write-Host ""

$previousErrorActionPreference = $ErrorActionPreference
$output = [System.Collections.Generic.List[string]]::new()
$exitCode = -1
$reportWriter = $null
try {
    $reportWriter = [System.IO.StreamWriter]::new($report, $true, [System.Text.Encoding]::UTF8)
    $reportWriter.AutoFlush = $true
    $ErrorActionPreference = "Continue"
    & dotnet @argsList 2>&1 | ForEach-Object {
        $line = $_.ToString()
        [void]$output.Add($line)
        Write-Host $line
        $reportWriter.WriteLine($line)
    }
    $exitCode = $LASTEXITCODE
}
finally {
    if ($null -ne $reportWriter) { $reportWriter.Dispose() }
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($exitCode -ne 0) {
    throw "The 1,000,000-record stress test failed with exit code $exitCode. See $report"
}
$requiredPassMarker = if ($GenerateOnly.IsPresent) { "Persistent stress corpus generation: PASS" } else { "Million stress test: PASS" }
if (-not ($output | Where-Object { $_ -eq $requiredPassMarker })) {
    throw "The stress harness exited successfully but did not report '$requiredPassMarker'. See $report"
}

Write-Host "$modeLabel`: PASS" -ForegroundColor Green
Write-Host "Report: $report" -ForegroundColor Green
