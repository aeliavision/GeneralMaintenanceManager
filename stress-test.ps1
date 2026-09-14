param(
    [long]$TotalRecords = 1000000,
    [int]$WorkOrders = 100000,
    [int]$FromYear = ((Get-Date).Year - 5),
    [int]$ToYear = (Get-Date).Year,
    [int]$LookupSamples = 200,
    [int]$Workers = 8,
    [int]$QueriesPerWorker = 25,
    [int]$Seed = 20260909,
    [int]$MaxWorkingSetMb = 1024,
    [string]$DataPath = "",
    [switch]$BenchmarkOnly,
    [switch]$GenerateOnly,
    [switch]$Reset,
    [switch]$IncludeBackup,
    [switch]$IncludeActivity
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot "tools\GeneralMaintenanceManager.ScaleHarness\GeneralMaintenanceManager.ScaleHarness.csproj"

function Assert-RequiredSdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet.exe was not found on PATH."
    }

    $globalJsonPath = Join-Path $projectRoot "global.json"
    $globalJson = Get-Content -Path $globalJsonPath -Raw | ConvertFrom-Json
    $requiredText = [string]$globalJson.sdk.version
    $actualText = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet --version failed with exit code $LASTEXITCODE."
    }

    $required = [Version]$requiredText
    $actual = [Version]$actualText
    if ($actual.Major -ne $required.Major -or $actual.Minor -ne $required.Minor -or $actual -lt $required) {
        throw "GMM requires .NET SDK $requiredText or a compatible newer .NET $($required.Major).$($required.Minor) SDK selected by global.json. Found $actualText."
    }

    return $actualText
}

function Resolve-ProjectPath([string]$PathText) {
    if ([string]::IsNullOrWhiteSpace($PathText)) { return "" }
    if ([System.IO.Path]::IsPathRooted($PathText)) {
        return [System.IO.Path]::GetFullPath($PathText)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $projectRoot $PathText))
}

function Test-DirectoryHasContent([string]$PathText) {
    if (-not (Test-Path -LiteralPath $PathText)) { return $false }
    return (@(Get-ChildItem -LiteralPath $PathText -Force -ErrorAction SilentlyContinue).Count -gt 0)
}

function Test-StressCorpusShape([string]$PathText) {
    if ([string]::IsNullOrWhiteSpace($PathText) -or -not (Test-Path -LiteralPath $PathText -PathType Container)) {
        return $false
    }

    $masterDatabase = Join-Path $PathText "MaintenanceManager_Master.db"
    $yearsDirectory = Join-Path $PathText "Years"
    return (Test-Path -LiteralPath $masterDatabase -PathType Leaf) -and
        (Test-Path -LiteralPath $yearsDirectory -PathType Container)
}


function Get-YearRecordCount([int]$Year) {
    $yearCount = $ToYear - $FromYear + 1
    $baseCount = [long][Math]::Floor($TotalRecords / $yearCount)
    $remainder = [long]($TotalRecords % $yearCount)
    $index = $Year - $FromYear
    $extra = if ($index -lt $remainder) { [long]1 } else { [long]0 }
    return ([long]$baseCount + [long]$extra)
}

function Get-AnnualDistributionText {
    $parts = for ($year = $FromYear; $year -le $ToYear; $year++) {
        "${year}=$((Get-YearRecordCount $year).ToString('N0'))"
    }
    return ($parts -join ', ')
}

function Resolve-ProjectLocalStressDataPath([string]$PathText) {
    $stressRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "release\stress-runs"))
    $candidate = if ([string]::IsNullOrWhiteSpace($PathText)) {
        [System.IO.Path]::GetFullPath((Join-Path $stressRoot "GMM-Full-Production\Data"))
    }
    else {
        Resolve-ProjectPath $PathText
    }

    $trimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $stressPrefix = $stressRoot.TrimEnd($trimChars) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($stressPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Stress DataPath must stay inside the current project under '$stressRoot'. Requested: $candidate"
    }

    return [System.IO.Path]::GetFullPath($candidate)
}

function Assert-BenchmarkCorpusExists([string]$PathText) {
    if (Test-StressCorpusShape $PathText) {
        return
    }

    $reason = if (-not (Test-Path -LiteralPath $PathText -PathType Container)) {
        "does not exist"
    }
    elseif (-not (Test-DirectoryHasContent $PathText)) {
        "is empty"
    }
    else {
        "does not contain MaintenanceManager_Master.db and a Years directory"
    }

    $relative = ".\release\stress-runs\GMM-Full-Production\Data"
    throw "Benchmark-only DataPath '$PathText' $reason.`nStress data is intentionally project-local and is never searched for in sibling projects.`nCreate/reuse the corpus inside this project first by running the normal stress command without -BenchmarkOnly, or generate it only with:`n  .\stress-test.ps1 -GenerateOnly -DataPath `"$relative`" -TotalRecords $TotalRecords -WorkOrders $WorkOrders -FromYear $FromYear -ToYear $ToYear$(if ($IncludeActivity.IsPresent) { ' -IncludeActivity' } else { '' })`nThen rerun -BenchmarkOnly against the same project-local Data path."
}

function Read-StressMarker([string]$MarkerPath) {
    $result = @{}
    if (-not (Test-Path -LiteralPath $MarkerPath)) { return $result }

    foreach ($line in Get-Content -LiteralPath $MarkerPath) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) {
            $key = $line.Substring(0, $separator).Trim()
            $value = $line.Substring($separator + 1).Trim()
            $result[$key] = $value
        }
    }
    return $result
}

function Write-StressMarker([string]$MarkerPath, [string]$Status) {
    @(
        "General Maintenance Manager persistent stress corpus",
        "Status=$Status",
        "GeneratedUtc=$([DateTimeOffset]::UtcNow.ToString('O'))",
        "FromYear=$FromYear",
        "ToYear=$ToYear",
        "TotalRecords=$TotalRecords",
        "AnnualDistribution=$(Get-AnnualDistributionText)",
        "WorkOrders=$WorkOrders",
        "IncludeActivity=$($IncludeActivity.IsPresent)",
        "Seed=$Seed"
    ) | Set-Content -LiteralPath $MarkerPath -Encoding UTF8
}

function Assert-CompatibleMarker([string]$MarkerPath) {
    if (-not (Test-Path -LiteralPath $MarkerPath)) {
        throw "The DataPath is not a marked GMM stress corpus: $MarkerPath"
    }

    $marker = Read-StressMarker $MarkerPath
    $expected = @{
        Status = "Ready"
        FromYear = $FromYear.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        ToYear = $ToYear.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        TotalRecords = $TotalRecords.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        WorkOrders = $WorkOrders.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        IncludeActivity = $IncludeActivity.IsPresent.ToString()
        Seed = $Seed.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }

    $differences = @()
    foreach ($key in $expected.Keys) {
        if (-not $marker.ContainsKey($key) -or $marker[$key] -ne $expected[$key]) {
            $actual = if ($marker.ContainsKey($key)) { $marker[$key] } else { "<missing>" }
            $differences += "$key expected '$($expected[$key])' but found '$actual'"
        }
    }

    if ($differences.Count -gt 0) {
        throw "Existing stress corpus parameters do not match this run:`n$($differences -join [Environment]::NewLine)`nUse the matching parameters or rerun with -Reset."
    }
}

function Invoke-ScaleHarness {
    param(
        [Parameter(Mandatory=$true)][string]$Stage,
        [Parameter(Mandatory=$true)][string[]]$HarnessArguments,
        [Parameter(Mandatory=$true)][string]$RequiredPassMarker,
        [Parameter(Mandatory=$true)][string]$ReportPath
    )

    Write-Host ""
    Write-Host "=== $Stage ===" -ForegroundColor Cyan
    $dotnetArgs = @("run", "--project", $project, "--configuration", "Release", "--") + $HarnessArguments
    $captured = [System.Collections.Generic.List[string]]::new()
    $writer = $null
    $previousErrorActionPreference = $ErrorActionPreference
    $exitCode = -1

    try {
        $writer = [System.IO.StreamWriter]::new($ReportPath, $true, [System.Text.Encoding]::UTF8)
        $writer.AutoFlush = $true
        $writer.WriteLine("")
        $writer.WriteLine("=== $Stage ===")
        $writer.WriteLine("Command: dotnet $($dotnetArgs -join ' ')")
        $ErrorActionPreference = "Continue"
        & dotnet @dotnetArgs 2>&1 | ForEach-Object {
            $line = $_.ToString()
            [void]$captured.Add($line)
            Write-Host $line
            $writer.WriteLine($line)
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        throw "$Stage failed with exit code $exitCode. See $ReportPath"
    }
    if (-not ($captured | Where-Object { $_ -eq $RequiredPassMarker })) {
        throw "$Stage exited successfully but did not report '$RequiredPassMarker'. See $ReportPath"
    }

    Write-Host "$Stage`: PASS" -ForegroundColor Green
}

if ($BenchmarkOnly.IsPresent -and $GenerateOnly.IsPresent) {
    throw "-BenchmarkOnly and -GenerateOnly are mutually exclusive."
}
if ($BenchmarkOnly.IsPresent -and $Reset.IsPresent) {
    throw "-BenchmarkOnly cannot be combined with -Reset because benchmark-only mode must not recreate data."
}
if ($FromYear -gt $ToYear) { throw "FromYear must be <= ToYear." }
if (($ToYear - $FromYear + 1) -gt 50) { throw "Stress generation is limited to 50 annual databases per run." }
if ($TotalRecords -lt 1 -or $TotalRecords -gt 99999999) { throw "TotalRecords must be between 1 and 99,999,999." }
if ($WorkOrders -lt 0 -or $WorkOrders -gt 999999) { throw "WorkOrders must be between 0 and 999,999." }
if ($LookupSamples -lt 1 -or $LookupSamples -gt 10000) { throw "LookupSamples must be between 1 and 10,000." }
if ($Workers -lt 1 -or $Workers -gt 64) { throw "Workers must be between 1 and 64." }
if ($QueriesPerWorker -lt 1 -or $QueriesPerWorker -gt 1000) { throw "QueriesPerWorker must be between 1 and 1,000." }
if ($MaxWorkingSetMb -lt 1) { throw "MaxWorkingSetMb must be greater than zero." }

Push-Location $projectRoot
try {
    $sdkVersion = Assert-RequiredSdk

    $DataPath = Resolve-ProjectLocalStressDataPath $DataPath
    if ($BenchmarkOnly.IsPresent) {
        Assert-BenchmarkCorpusExists $DataPath
    }

    $runDirectory = Split-Path -Parent $DataPath
    $markerPath = Join-Path $DataPath ".gmm-stress-run"
    $reportPath = Join-Path $runDirectory "StressReport.txt"
    $yearCount = $ToYear - $FromYear + 1
    $totalMaintenance = [long]$TotalRecords
    $benchmarkYearRecords = Get-YearRecordCount $ToYear
    if ($benchmarkYearRecords -lt 50000) {
        throw "The benchmark year must contain at least 50,000 Maintenance rows. Increase -TotalRecords or reduce the year range."
    }
    $distributionText = Get-AnnualDistributionText

    if ($Reset.IsPresent -and (Test-Path -LiteralPath $DataPath)) {
        if ((Test-DirectoryHasContent $DataPath) -and -not (Test-Path -LiteralPath $markerPath)) {
            throw "Refusing to reset unmarked directory: $DataPath`nOnly Data directories containing .gmm-stress-run can be deleted by this script."
        }
        Remove-Item -LiteralPath $DataPath -Recurse -Force
        $backupSibling = "$DataPath.Backups"
        if (Test-Path -LiteralPath $backupSibling) {
            Remove-Item -LiteralPath $backupSibling -Recurse -Force
        }
    }

    if ((Test-DirectoryHasContent $DataPath) -and -not (Test-Path -LiteralPath $markerPath) -and -not $BenchmarkOnly.IsPresent) {
        throw "Refusing to use unmarked Data directory: $DataPath`nChoose a fresh -DataPath or a GMM stress directory containing .gmm-stress-run."
    }

    New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
    if (-not $BenchmarkOnly.IsPresent) {
        New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
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
        }
        catch { $storage = "unknown" }
    }
    catch {
        $windows = [System.Environment]::OSVersion.VersionString
        $ramGb = "unknown"
    }

    $mode = if ($GenerateOnly.IsPresent) { "GENERATE ONLY" } elseif ($BenchmarkOnly.IsPresent) { "BENCHMARK ONLY" } else { "GENERATE/REUSE + BENCHMARK" }
    @(
        "General Maintenance Manager - Stress Test",
        "Run: $((Get-Date).ToString('u'))",
        "Mode: $mode",
        "Windows: $windows",
        "Machine: $([System.Environment]::MachineName)",
        "CPU: $processorName",
        "Processor count: $([System.Environment]::ProcessorCount)",
        "RAM GB: $ramGb",
        "Storage: $storage",
        ".NET SDK: $sdkVersion",
        "DataPath: $DataPath",
        "Years: $FromYear-$ToYear ($yearCount annual databases)",
        "Total Maintenance records: $totalMaintenance",
        "Annual distribution: $distributionText",
        "Benchmark year ($ToYear) records: $benchmarkYearRecords",
        "Master Work Orders: $WorkOrders",
        "Include activity: $($IncludeActivity.IsPresent)",
        "Include backup/restore benchmark: $($IncludeBackup.IsPresent)",
        "Lookup samples: $LookupSamples",
        "Workers: $Workers",
        "Queries per worker: $QueriesPerWorker",
        "Max working set MB: $MaxWorkingSetMb",
        "Seed: $Seed",
        ""
    ) | Set-Content -LiteralPath $reportPath -Encoding UTF8

    Write-Host "Using .NET SDK: $sdkVersion" -ForegroundColor Cyan
    Write-Host "General Maintenance Manager stress test" -ForegroundColor Cyan
    Write-Host "Mode: $mode"
    Write-Host "DataPath: $DataPath"
    Write-Host "Years: $FromYear-$ToYear ($yearCount annual databases)"
    Write-Host "Total Maintenance records: $($totalMaintenance.ToString('N0'))"
    Write-Host "Annual distribution: $distributionText"
    Write-Host "Benchmark year ($ToYear) records: $($benchmarkYearRecords.ToString('N0'))"
    Write-Host "Master Work Orders: $($WorkOrders.ToString('N0'))"
    Write-Host "Activity rows: $(if ($IncludeActivity.IsPresent) { 'enabled' } else { 'disabled' })"
    Write-Host "Backup/restore benchmark: $(if ($IncludeBackup.IsPresent) { 'enabled' } else { 'disabled' })"
    Write-Host "Report: $reportPath"

    $hasReadyCorpus = $false
    if (Test-Path -LiteralPath $markerPath) {
        Assert-CompatibleMarker $markerPath
        $hasReadyCorpus = $true
    }

    if ($BenchmarkOnly.IsPresent) {
        if (-not $hasReadyCorpus) {
            Write-Host ""
            Write-Host "Stress marker is missing. Verifying the existing corpus before adopting it..." -ForegroundColor Yellow
            $verifyArgs = @(
                "--verify-existing-corpus",
                "--start-year=$FromYear",
                "--end-year=$ToYear",
                "--total-records=$TotalRecords",
                "--work-orders=$WorkOrders",
                "--root=$DataPath"
            )
            if ($IncludeActivity.IsPresent) { $verifyArgs += "--include-activity" }
            Invoke-ScaleHarness `
                -Stage "VERIFY EXISTING UNMARKED STRESS CORPUS" `
                -HarnessArguments $verifyArgs `
                -RequiredPassMarker "Existing stress corpus verification: PASS" `
                -ReportPath $reportPath
            Write-StressMarker $markerPath "Ready"
            Assert-CompatibleMarker $markerPath
            $hasReadyCorpus = $true
            Write-Host "Verified corpus adopted and marked for safe benchmark reuse." -ForegroundColor Green
        }
    }
    elseif (-not $hasReadyCorpus) {
        Write-StressMarker $markerPath "Generating"
        $generateArgs = @(
            "--generate-fixture",
            "--start-year=$FromYear",
            "--end-year=$ToYear",
            "--total-records=$TotalRecords",
            "--work-orders=$WorkOrders",
            "--seed=$Seed",
            "--root=$DataPath"
        )
        if ($IncludeActivity.IsPresent) { $generateArgs += "--include-activity" }

        try {
            Invoke-ScaleHarness `
                -Stage "GENERATE MULTI-YEAR STRESS CORPUS" `
                -HarnessArguments $generateArgs `
                -RequiredPassMarker "Multi-year copyable database fixture: PASS" `
                -ReportPath $reportPath
            Write-StressMarker $markerPath "Ready"
            $hasReadyCorpus = $true
        }
        catch {
            Write-Host "Generation failed. Partial marked stress data was preserved for diagnosis: $DataPath" -ForegroundColor Yellow
            Write-Host "Use -Reset to safely recreate this marked stress directory." -ForegroundColor Yellow
            throw
        }
    }
    else {
        Write-Host ""
        Write-Host "Existing compatible marked stress corpus found; generation skipped." -ForegroundColor Green
    }

    if ($GenerateOnly.IsPresent) {
        Write-Host ""
        Write-Host "Stress corpus generation: PASS" -ForegroundColor Green
        Write-Host "Reusable Data folder: $DataPath" -ForegroundColor Green
        Write-Host "Report: $reportPath" -ForegroundColor Green
        return
    }

    $benchmarkArgs = @(
        "--benchmark-only",
        "--records=$benchmarkYearRecords",
        "--total-records=$TotalRecords",
        "--start-year=$FromYear",
        "--end-year=$ToYear",
        "--year=$ToYear",
        "--root=$DataPath",
        "--keep",
        "--work-orders=$WorkOrders",
        "--lookups=$LookupSamples",
        "--workers=$Workers",
        "--queries-per-worker=$QueriesPerWorker",
        "--seed=$Seed",
        "--max-working-set-mb=$MaxWorkingSetMb"
    )
    if (-not $IncludeBackup.IsPresent) { $benchmarkArgs += "--skip-backup-restore" }

    Invoke-ScaleHarness `
        -Stage "BENCHMARK PRODUCTION SERVICES" `
        -HarnessArguments $benchmarkArgs `
        -RequiredPassMarker "Production stress test: PASS" `
        -ReportPath $reportPath

    Write-Host ""
    Write-Host "GMM stress test: PASS" -ForegroundColor Green
    Write-Host "Reusable Data folder: $DataPath" -ForegroundColor Green
    Write-Host "Report: $reportPath" -ForegroundColor Green
    if (-not $IncludeBackup.IsPresent) {
        Write-Host "Backup/restore stress was skipped. Re-run with -IncludeBackup when you want that expensive phase." -ForegroundColor DarkGray
    }
}
finally {
    Pop-Location
}
