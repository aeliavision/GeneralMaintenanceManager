param(
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [int]$Year = (Get-Date).Year,
    [int]$WorkOrders = 100000,
    [int]$Lookups = 200,
    [int]$Workers = 8,
    [int]$QueriesPerWorker = 25,
    [int]$MaxWorkingSetMb = 1024,
    [switch]$SkipBackupRestore
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$data = [System.IO.Path]::GetFullPath($DataDirectory)
$stressRoot = [System.IO.Path]::GetFullPath((Join-Path $root "release\stress-runs"))
$prefix = $stressRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $data.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Benchmark DataDirectory must stay inside the current project under '$stressRoot'. Requested: $data"
}
if (-not (Test-Path -LiteralPath (Join-Path $data ".gmm-stress-run"))) {
    throw "Refusing to benchmark unmarked data directory: $data"
}
& (Join-Path $scriptDir "run-million-stress-test.ps1") `
    -Year $Year -DataRoot $data -BenchmarkOnly -KeepData `
    -WorkOrders $WorkOrders -Lookups $Lookups -Workers $Workers `
    -QueriesPerWorker $QueriesPerWorker -MaxWorkingSetMb $MaxWorkingSetMb `
    -SkipBackupRestore:$SkipBackupRestore.IsPresent
if ($LASTEXITCODE -ne 0) { throw "Persistent corpus benchmark failed." }
