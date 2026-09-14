param(
    [switch]$FrameworkDependent,
    [string]$Configuration = "Release",
    [switch]$SkipZip,
    [switch]$SkipVerification
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir
$publisher = Join-Path $scriptDir "publish-windows.ps1"

& $publisher -Configuration $Configuration -Architectures @("win-x64") -FrameworkDependent:$FrameworkDependent -SkipZip:$SkipZip -SkipVerification:$SkipVerification
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
