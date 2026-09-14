param(
    [int]$Records = 100000,
    [int]$Year = (Get-Date).Year,
    [switch]$Million,
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if ($Million.IsPresent) {
    $millionScript = Join-Path $root "run-million-stress-test.ps1"
    if (-not (Test-Path -LiteralPath $millionScript)) {
        throw "Million-record stress wrapper not found: $millionScript"
    }

    if ($KeepData.IsPresent) {
        & $millionScript -Year $Year -KeepData
    }
    else {
        & $millionScript -Year $Year
    }

    if ($LASTEXITCODE -ne 0) { throw "Million-record stress harness failed." }
    return
}

$project = Join-Path $root "tools\GeneralMaintenanceManager.ScaleHarness\GeneralMaintenanceManager.ScaleHarness.csproj"
$argsList = @("run", "--project", $project, "-c", "Release", "--", "--year=$Year", "--records=$Records")
if ($KeepData.IsPresent) { $argsList += "--keep" }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet.exe was not found on PATH."
}

$previousErrorActionPreference = $ErrorActionPreference
$exitCode = -1
try {
    $ErrorActionPreference = "Continue"
    & dotnet @argsList 2>&1 | ForEach-Object { $_.ToString() }
    $exitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($exitCode -ne 0) { throw "Scale harness failed with exit code $exitCode." }
