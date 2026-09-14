param(
    [string]$Configuration = "Release",
    [int]$ScaleRecords = 100000,
    [switch]$RunMillionScale,
    [switch]$ManualAcceptanceConfirmed
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root "GeneralMaintenanceManager.slnx"
$report = Join-Path $root "CERTIFICATION_RESULTS.txt"

if ($ScaleRecords -lt 1 -or $ScaleRecords -gt 9999999) {
    throw "ScaleRecords must be between 1 and 9,999,999."
}

function Assert-SqliteHeader([string]$Path) {
    if (-not (Test-Path $Path)) { throw "Expected database was not created: $Path" }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 16) { throw "Database is too small to be a valid SQLite file: $Path" }
    $header = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 15)
    if ($header -ne "SQLite format 3") { throw "Database does not have a SQLite format-3 header: $Path" }
}

function Add-EnvironmentEvidence([System.Collections.Generic.List[string]]$Lines) {
    try {
        $os = Get-CimInstance Win32_OperatingSystem
        $Lines.Add("Windows: $($os.Caption) $($os.Version) build $($os.BuildNumber)")
        $Lines.Add("OS architecture: $($os.OSArchitecture)")
    }
    catch {
        $Lines.Add("Windows: $([System.Environment]::OSVersion.VersionString)")
        $Lines.Add("OS architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)")
    }
    $Lines.Add("Machine: $([System.Environment]::MachineName)")
    $Lines.Add("Processor count: $([System.Environment]::ProcessorCount)")
}

function Test-RuntimeSmoke([string]$PublishPath, [string]$RuntimeId, [System.Collections.Generic.List[string]]$Lines) {
    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) "GeneralMaintenanceManager-runtime-smoke-$RuntimeId-$PID"
    try {
        if (Test-Path $smokeRoot) { Remove-Item $smokeRoot -Recurse -Force }
        Copy-Item -Path $PublishPath -Destination $smokeRoot -Recurse -Force
        $exe = Join-Path $smokeRoot "GeneralMaintenanceManager.exe"
        $process = Start-Process -FilePath $exe -ArgumentList "--ui-smoke-test" -PassThru -Wait
        if ($process.ExitCode -ne 0) { throw "$RuntimeId packaged UI smoke test exited with code $($process.ExitCode)." }

        $master = Join-Path $smokeRoot "Data\MaintenanceManager_Master.db"
        $annual = Join-Path $smokeRoot ("Data\Years\Maintenance_{0:0000}.db" -f (Get-Date).Year)
        Assert-SqliteHeader $master
        Assert-SqliteHeader $annual

        $unexpectedAnnual = @(Get-ChildItem -Path (Join-Path $smokeRoot "Data\Years") -Filter "Maintenance_*.db" -File | Where-Object { $_.FullName -ne $annual })
        if ($unexpectedAnnual.Count -ne 0) { throw "$RuntimeId smoke created unexpected annual databases: $($unexpectedAnnual.Name -join ', ')" }

        $startupLog = Join-Path $smokeRoot "Data\SystemLogs\startup-performance.log"
        if (-not (Test-Path $startupLog)) { throw "$RuntimeId smoke did not create startup-performance.log." }
        $startupText = Get-Content -Path $startupLog -Raw
        foreach ($stage in @("Smoke database initialize", "Smoke startup health", "Smoke recovery", "Smoke MainWindow constructed")) {
            if ($startupText -notmatch [regex]::Escape($stage)) { throw "$RuntimeId startup log is missing stage '$stage'." }
        }

        $Lines.Add("$RuntimeId packaged XAML/DI/startup smoke: PASS")
        $Lines.Add("$RuntimeId startup-performance log: PASS")
        $Lines.Add("$RuntimeId fresh master + current-year annual database creation: PASS")
    }
    finally {
        if (Test-Path $smokeRoot) { Remove-Item $smokeRoot -Recurse -Force }
    }
}

function Add-ScaleEvidence(
    [string]$Label,
    [scriptblock]$Invocation,
    [System.Collections.Generic.List[string]]$Lines
) {
    $output = [System.Collections.Generic.List[string]]::new()
    & $Invocation 2>&1 | ForEach-Object {
        $item = $_.ToString()
        [void]$output.Add($item)
        Write-Host $item
    }
    foreach ($prefix in @("Target records:", "Seed time:", "First 200-row page:", "Second 200-row page:", "Filtered aggregate:", "Paging overlap:", "Scale harness:")) {
        $metric = $output | Where-Object { $_.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -Last 1
        if ($null -ne $metric) { $Lines.Add("$Label $metric") }
    }
    if (-not ($output | Where-Object { $_ -eq "Scale harness: PASS" })) {
        throw "$Label scale harness did not report PASS."
    }
    $Lines.Add("$Label scale harness: PASS")
}

function Add-MillionStressEvidence(
    [System.Collections.Generic.List[string]]$Lines
) {
    $output = [System.Collections.Generic.List[string]]::new()
    & .\stress-test.ps1 -Reset -TotalRecords 1000000 -WorkOrders 100000 -FromYear ((Get-Date).Year - 5) -ToYear (Get-Date).Year -IncludeActivity -IncludeBackup 2>&1 | ForEach-Object {
        $item = $_.ToString()
        [void]$output.Add($item)
        Write-Host $item
    }
    foreach ($prefix in @(
        "Total Maintenance records:",
        "Annual distribution:",
        "Benchmark year",
        "Target records:",
        "Verified rows:",
        "Invalid rows:",
        "Database quick_check:",
        "Database size:",
        "First 200-row page:",
        "50 keyset pages",
        "Direct MNT lookup",
        "Location-filtered page:",
        "Maintenance-type filtered page:",
        "Asset-filtered page:",
        "Global valid aggregate:",
        "Location aggregate:",
        "Maintenance-type aggregate:",
        "Dashboard current-month aggregate p95:",
        "Dashboard full summary warm p95:",
        "FTS text search x25:",
        "Concurrent read burst:",
        "Work Orders:",
        "Peak working set:",
        "Total stress time:",
        "Production stress test:"
    )) {
        $metric = $output | Where-Object { $_.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -Last 1
        if ($null -ne $metric) { $Lines.Add("Scale-1000000-total $metric") }
    }
    if (-not ($output | Where-Object { $_ -eq "GMM stress test: PASS" })) {
        throw "The six-year 1,000,000-total stress test did not report PASS."
    }
    $stressReport = Join-Path $root "release\stress-runs\GMM-Full-Production\StressReport.txt"
    if (-not (Test-Path $stressReport)) { throw "The production stress report was not created: $stressReport" }
    $stressHash = (Get-FileHash -Path $stressReport -Algorithm SHA256).Hash.ToLowerInvariant()
    $Lines.Add("Scale-1000000-total stress report: $stressReport")
    $Lines.Add("Scale-1000000-total stress report SHA-256: $stressHash")
    $Lines.Add("Scale-1000000-total six-year production stress: PASS")
}

function Test-SourcePackage(
    [string]$ZipPath,
    [System.Collections.Generic.List[string]]$Lines
) {
    if (-not (Test-Path $ZipPath)) { throw "Source package was not created: $ZipPath" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @(
            "GeneralMaintenanceManager.slnx",
            "USER_GUIDE.md",
            "DEVELOPER_GUIDE.md",
            "src/GeneralMaintenanceManager.App/GeneralMaintenanceManager.App.csproj",
            "src/GeneralMaintenanceManager.Core/GeneralMaintenanceManager.Core.csproj",
            "src/GeneralMaintenanceManager.Infrastructure/GeneralMaintenanceManager.Infrastructure.csproj",
            "src/GeneralMaintenanceManager.Infrastructure/Data/AnnualDbContext.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/AnnualMaintenanceDatabaseManager.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/DatabaseContextFactory.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/DatabaseInitializer.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/DataPaths.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/MaintenanceReference.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/MasterDbContext.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/ReferenceSequenceRows.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/SchemaInfoRow.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/ProductionSchemaPolicy.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/ProductionMigrationCoordinator.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/SchemaMigrationJournalRow.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/SqliteMaintenanceService.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Data/SqlitePerformanceInterceptor.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Services/SingleInstanceGuard.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Services/DatabaseActivityGate.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Services/StartupPerformanceLogger.cs",
            "src/GeneralMaintenanceManager.Infrastructure/Services/DatabaseMaintenanceService.cs",
            "src/GeneralMaintenanceManager.App/Services/PrintSettings.cs",
            "tests/GeneralMaintenanceManager.Tests/GeneralMaintenanceManager.Tests.csproj",
            "tests/GeneralMaintenanceManager.Tests/DatabaseHardeningCompletionTests.cs",
            "tests/GeneralMaintenanceManager.Tests/MedEquipArchitectureAdoptionTests.cs",
            "tools/GeneralMaintenanceManager.ScaleHarness/GeneralMaintenanceManager.ScaleHarness.csproj",
            "run-million-stress-test.ps1",
            "generate-multiyear-stress-databases.ps1",
            "generate-live-test-database.ps1",
            "benchmark-live-test-database.ps1",
            "install-live-test-database.ps1",
            "certify-database-remediation.ps1"
        )) {
            if ($required -notin $entries) { throw "Source ZIP is missing required entry '$required'." }
        }

        $forbidden = @($entries | Where-Object {
            $_ -match '(^|/)(?:bin|obj|TestResults)(/|$)' -or
            $_ -match '^(?:release|Data|Backups)(/|$)' -or
            $_ -match '(?i)\.(?:db|sqlite|sqlite3|user|suo|zip|log)$' -or
            $_ -match '(?i)\.db-(?:wal|shm)$' -or
            $_ -eq 'CERTIFICATION_RESULTS.txt'
        })
        if ($forbidden.Count -ne 0) { throw "Source ZIP contains generated/runtime content: $($forbidden -join ', ')" }
        $Lines.Add("Source ZIP layout/hygiene: PASS")
        $Lines.Add("Source ZIP entries: $($entries.Count)")
    }
    finally { $archive.Dispose() }

    $hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $Lines.Add("Source ZIP SHA-256: $hash")
}

Push-Location $root
$lines = New-Object System.Collections.Generic.List[string]
try {
    $lines.Add("General Maintenance Manager production certification")
    $lines.Add("Run: " + (Get-Date).ToString("u"))
    Add-EnvironmentEvidence $lines

    & .\verify-maintenance-first.ps1 -AllowGeneratedArtifacts
    $lines.Add("Current-architecture static verification: PASS")

    [xml]$props = Get-Content -Path (Join-Path $root "Directory.Build.props") -Raw
    $version = ([string]$props.Project.PropertyGroup.Version).Trim()
    if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not read Version from Directory.Build.props." }
    $lines.Add("Application version: $version")

    # Create and inspect the clean source package before build artifacts exist.
    & .\package-source.ps1
    $sourceZip = Join-Path $root "release\packages\GeneralMaintenanceManager-v$version-source.zip"
    Test-SourcePackage $sourceZip $lines

    $sdk = (dotnet --version).Trim()
    if ([Version]$sdk -lt [Version]"10.0.0") { throw ".NET SDK 10 or newer is required. Found $sdk." }
    $lines.Add(".NET SDK: $sdk")

    dotnet clean $solution -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Solution clean failed." }
    $lines.Add("Clean: PASS")

    dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw "Solution restore failed." }
    $lines.Add("Restore: PASS")

    dotnet build $solution -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed." }
    $lines.Add("Release build: PASS")

    dotnet test $solution -c $Configuration --no-build --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { throw "Automated tests failed." }
    $lines.Add("Automated integrity tests: PASS")

    Add-ScaleEvidence "Scale-$ScaleRecords" { & .\run-scale-harness.ps1 -Records $ScaleRecords } $lines
    if ($RunMillionScale.IsPresent) {
        Add-MillionStressEvidence $lines
    }
    else {
        $lines.Add("Six-year 1,000,000-total production stress: PENDING (rerun with -RunMillionScale)")
    }

    $userGuideText = Get-Content -Path (Join-Path $root "USER_GUIDE.md") -Raw
    $developerGuideText = Get-Content -Path (Join-Path $root "DEVELOPER_GUIDE.md") -Raw
    foreach ($policyText in @('portable writable-folder', 'Program Files', 'Data\SystemLogs')) {
        if ($userGuideText -notmatch [regex]::Escape($policyText) -and $developerGuideText -notmatch [regex]::Escape($policyText)) {
            throw "Portable deployment policy text is missing: $policyText"
        }
    }
    $lines.Add("Portable writable-folder policy: PASS")

    & .\publish-windows.ps1 -Configuration $Configuration -SkipVerification
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    foreach ($rid in @("win-x64", "win-x86")) {
        $publish = Join-Path $root "release\$rid"
        $exe = Join-Path $publish "GeneralMaintenanceManager.exe"
        $zip = Join-Path $root "release\packages\GeneralMaintenanceManager-v$version-$rid.zip"
        if (-not (Test-Path $exe)) { throw "$rid executable was not created." }
        if (-not (Test-Path $zip)) { throw "$rid distribution ZIP was not created." }

        $preRunDatabases = @(Get-ChildItem -Path (Join-Path $publish "Data") -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.db' })
        if ($preRunDatabases.Count -ne 0) { throw "$rid publish contains databases before first run." }

        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
            foreach ($requiredEntry in @("GeneralMaintenanceManager.exe", "Data/", "Data/Years/", "Data/ImportLogs/", "Data/SystemLogs/", "Backups/")) {
                if ($requiredEntry -notin $entryNames) { throw "$rid ZIP is missing required entry '$requiredEntry'." }
            }
            if ($entryNames | Where-Object { $_ -match '(?i)\.(?:db|sqlite|sqlite3)$' -or $_ -match '(?i)\.db-(?:wal|shm)$' }) {
                throw "$rid ZIP unexpectedly contains a runtime/development database."
            }
        }
        finally { $archive.Dispose() }

        Test-RuntimeSmoke $publish $rid $lines
        $lines.Add("$rid publish + ZIP layout: PASS")
        $hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines.Add("$rid ZIP SHA-256: $hash")
    }

    $lines.Add("Certification scope note: this report proves only the Windows machine on which it was executed. Run and retain evidence on Windows 10 and Windows 11 before claiming both-platform runtime certification.")

    if (-not $ManualAcceptanceConfirmed.IsPresent -or -not $RunMillionScale.IsPresent) {
        $lines.Add("RESULT: AUTOMATED GATES PASS; FINAL MANUAL/REPRESENTATIVE-SCALE ACCEPTANCE PENDING")
        if (-not $ManualAcceptanceConfirmed.IsPresent) {
            $lines.Add("Pending: manual WPF navigation/workflows, EN/AR/RTL visual review, responsive-window review, and real printing acceptance.")
        }
        if (-not $RunMillionScale.IsPresent) {
            $lines.Add("Pending: representative six-year 1,000,000-total Maintenance stress run.")
        }
        $lines.Add("Rerun with -RunMillionScale and -ManualAcceptanceConfirmed only after those checks actually pass.")
        $lines | Set-Content -Path $report -Encoding UTF8
        Write-Host "Automated certification passed. Final manual/representative-scale acceptance remains pending." -ForegroundColor Yellow
        Write-Host "Report: $report" -ForegroundColor Green
        return
    }

    $lines.Add("Operator assertion: manual WPF interaction, printing, EN/AR/RTL and responsive-window acceptance completed on this machine: CONFIRMED")
    $lines.Add("Representative six-year 1,000,000-total Maintenance stress test: CONFIRMED PASS")
    $lines.Add("RESULT: FULL CURRENT-MACHINE RELEASE ACCEPTANCE PASS")
    $lines | Set-Content -Path $report -Encoding UTF8
    Write-Host "Full current-machine production acceptance passed for win-x64 and win-x86." -ForegroundColor Green
    Write-Host "Retain separate Windows 10 and Windows 11 evidence before claiming both-platform certification." -ForegroundColor Yellow
}
catch {
    $lines.Add("RESULT: FAIL")
    $lines.Add($_.Exception.ToString())
    $lines | Set-Content -Path $report -Encoding UTF8
    throw
}
finally {
    Pop-Location
}
