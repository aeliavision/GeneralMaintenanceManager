param(
    [switch]$FrameworkDependent,
    [string]$Configuration = "Release",
    [switch]$SkipZip,
    [switch]$SkipVerification
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publisher = Join-Path $root "publish-windows.ps1"

& $publisher -Configuration $Configuration -Architectures @("win-x64") -FrameworkDependent:$FrameworkDependent -SkipZip:$SkipZip -SkipVerification:$SkipVerification
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
