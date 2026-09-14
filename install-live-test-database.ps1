param(
    [Parameter(Mandatory=$true)][string]$SourceDataDirectory,
    [Parameter(Mandatory=$true)][string]$TargetDataDirectory
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$source = [System.IO.Path]::GetFullPath($SourceDataDirectory)
$target = [System.IO.Path]::GetFullPath($TargetDataDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $source ".gmm-stress-run"))) {
    throw "Source is not a marked GMM stress corpus: $source"
}
if (-not (Test-Path -LiteralPath (Join-Path $source "MaintenanceManager_Master.db"))) {
    throw "Source master database is missing: $source"
}
$parent = Split-Path -Parent $target
New-Item -ItemType Directory -Force -Path $parent | Out-Null
if (Test-Path -LiteralPath $target) {
    $backup = "$target.before-live-test-$((Get-Date).ToString('yyyyMMdd-HHmmss'))"
    Move-Item -LiteralPath $target -Destination $backup
    Write-Host "Existing target Data folder preserved as: $backup" -ForegroundColor Yellow
}
Copy-Item -LiteralPath $source -Destination $target -Recurse -Force
Write-Host "Live-test Data installed: $target" -ForegroundColor Green
