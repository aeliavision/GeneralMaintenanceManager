param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
[xml]$props = Get-Content -Path (Join-Path $root "Directory.Build.props") -Raw
$version = ([string]$props.Project.PropertyGroup.Version).Trim()
if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not read Version from Directory.Build.props." }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $root "release\packages" }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "GeneralMaintenanceManager-source-$version-$PID"
$zipPath = Join-Path $OutputDirectory "GeneralMaintenanceManager-v$version-source.zip"

try {
    & (Join-Path $root "verify-maintenance-first.ps1") -AllowGeneratedArtifacts
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    $robocopyArgs = @(
        $root, $stage, "/E", "/R:1", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS", "/NP",
        "/XD", ".git", ".vs", ".idea", ".vscode", "bin", "obj", "release", "TestResults",
        (Join-Path $root "Data"), (Join-Path $root "Backups"),
        "/XF", "*.db", "*.db-wal", "*.db-shm", "*.sqlite", "*.sqlite3", "*.user", "*.suo", "*.zip", "*.log", "CERTIFICATION_RESULTS.txt"
    )
    & robocopy @robocopyArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }

    # Critical source-tree completeness guard. Runtime Data folders are excluded, but the
    # Infrastructure\Data C# namespace is source code and must always be packaged.
    $requiredStagedSourceFiles = @(
        "src\GeneralMaintenanceManager.Infrastructure\Data\DatabaseInitializer.cs",
        "src\GeneralMaintenanceManager.Infrastructure\Data\MasterDbContext.cs",
        "src\GeneralMaintenanceManager.Infrastructure\Data\AnnualDbContext.cs",
        "src\GeneralMaintenanceManager.Infrastructure\Data\DatabaseContextFactory.cs",
        "src\GeneralMaintenanceManager.Infrastructure\Data\DataPaths.cs"
    )
    $missingStagedSourceFiles = @($requiredStagedSourceFiles | Where-Object { -not (Test-Path (Join-Path $stage $_)) })
    if ($missingStagedSourceFiles.Count -ne 0) {
        throw "Critical source files are missing from the staged source package: $($missingStagedSourceFiles -join ', ')"
    }

    # Package cleanliness is enforced against the staged output, not against the working tree.
    # This allows package-source.ps1 to be run after normal build/test commands have created bin/obj.
    $stagedGeneratedDirs = @(Get-ChildItem -Path $stage -Recurse -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -in @('bin','obj','TestResults') })
    if ($stagedGeneratedDirs.Count -ne 0) {
        throw "Generated build/test directories leaked into the staged source package: $($stagedGeneratedDirs.FullName -join ', ')"
    }
    $stagedForbiddenArtifacts = @(Get-ChildItem -Path $stage -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Extension -in @('.db','.sqlite','.sqlite3','.user','.suo','.log','.zip') -or $_.Name -match '\.db-(?:wal|shm)$'
    })
    if ($stagedForbiddenArtifacts.Count -ne 0) {
        throw "Runtime/generated artifacts leaked into the staged source package: $($stagedForbiddenArtifacts.FullName -join ', ')"
    }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Source ZIP: $zipPath" -ForegroundColor Green
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
