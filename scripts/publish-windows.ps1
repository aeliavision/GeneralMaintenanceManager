param(
    [switch]$FrameworkDependent,
    [string]$Configuration = "Release",
    [string[]]$Architectures = @("win-x64", "win-x86"),
    [switch]$SkipZip,
    [switch]$SkipVerification
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$project = Join-Path $root "src\GeneralMaintenanceManager.App\GeneralMaintenanceManager.App.csproj"
$releaseRoot = Join-Path $root "release"
$packagesRoot = Join-Path $releaseRoot "packages"
$selfContained = -not $FrameworkDependent.IsPresent

function Get-ProjectVersion {
    [xml]$props = Get-Content -Path (Join-Path $root "Directory.Build.props") -Raw
    $version = [string]$props.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not read Version from Directory.Build.props." }
    $version.Trim()
}

function New-PortableFolders([string]$OutputPath) {
    $data = Join-Path $OutputPath "Data"
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $data "Years") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $data "ImportLogs") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $data "SystemLogs") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $OutputPath "Backups") | Out-Null
}

function Assert-FreshPortableLayout([string]$OutputPath) {
    foreach ($required in @("Data", "Data\Years", "Data\ImportLogs", "Data\SystemLogs", "Backups")) {
        if (-not (Test-Path (Join-Path $OutputPath $required))) { throw "Publish is missing required folder '$required'." }
    }
    $unexpected = @(Get-ChildItem -Path (Join-Path $OutputPath "Data") -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Extension -in @('.db','.sqlite','.sqlite3') -or $_.Name -match '\.db-(?:wal|shm)$'
    })
    if ($unexpected.Count -ne 0) { throw "Publish contains development databases: $($unexpected.FullName -join ', ')" }
}

function Ensure-ZipDirectoryEntries([string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($entryName in @("Data/", "Data/Years/", "Data/ImportLogs/", "Data/SystemLogs/", "Backups/")) {
            if ($null -eq $archive.GetEntry($entryName)) { [void]$archive.CreateEntry($entryName) }
        }
    }
    finally { $archive.Dispose() }
}

Push-Location $root
try {
    if (-not $SkipVerification.IsPresent) { & (Join-Path $scriptDir "verify-maintenance-first.ps1") -AllowGeneratedArtifacts }
    $version = Get-ProjectVersion
    $supported = @("win-x64", "win-x86")
    foreach ($architecture in $Architectures) {
        if ($architecture -notin $supported) { throw "Unsupported architecture '$architecture'. Supported values: win-x64, win-x86." }
    }

    New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
    if (-not $SkipZip.IsPresent) { New-Item -ItemType Directory -Force -Path $packagesRoot | Out-Null }

    foreach ($runtimeId in $Architectures) {
        $output = Join-Path $releaseRoot $runtimeId
        if (Test-Path $output) { Remove-Item $output -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $output | Out-Null

        Write-Host "Publishing General Maintenance Manager $version for $runtimeId..." -ForegroundColor Cyan
        dotnet restore $project -r $runtimeId
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $runtimeId." }

        dotnet publish $project -c $Configuration -r $runtimeId `
            --self-contained $($selfContained.ToString().ToLowerInvariant()) --no-restore `
            -p:PublishSingleFile=false -p:PublishTrimmed=false -o $output
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $runtimeId." }

        New-PortableFolders $output
        Assert-FreshPortableLayout $output

        $exe = Join-Path $output "GeneralMaintenanceManager.exe"
        if (-not (Test-Path $exe)) { throw "Published executable was not created for $runtimeId." }
        if ($selfContained) {
            $sqliteNative = Get-ChildItem -Path $output -Recurse -Filter "e_sqlite3.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -eq $sqliteNative) { throw "SQLite native runtime was not found in the $runtimeId self-contained publish." }
        }

        if (-not $SkipZip.IsPresent) {
            $zipPath = Join-Path $packagesRoot "GeneralMaintenanceManager-v$version-$runtimeId.zip"
            if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
            Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zipPath -CompressionLevel Optimal
            Ensure-ZipDirectoryEntries $zipPath
            Write-Host "Package: $zipPath" -ForegroundColor Green
        }
        Write-Host "Published folder: $output" -ForegroundColor Green
    }

    Write-Host "Windows publishing complete. Packages contain an empty hybrid Data layout; databases are created on first run." -ForegroundColor Green
}
finally {
    Pop-Location
}
