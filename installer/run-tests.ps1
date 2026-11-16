# Runs all tests (dotnet test EasyPDF.slnx) before an installer is built: a failing test stops the build, so an
# installer only comes from a state whose tests pass. Takes a few minutes (the UI tests run headless).
# Called by every installer/*/build-*.ps1 script after cleaning bin/obj; installer/build-macos-on-macos.sh does the same itself.
# Skipped with -NoTests of the build scripts or the environment variable EASYPDF_NO_TESTS=1.
param([Parameter(Mandatory)] [string]$Brand)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ($env:EASYPDF_NO_TESTS) {
    Write-Host '== Tests skipped (-NoTests / EASYPDF_NO_TESTS)' -ForegroundColor Yellow
    return
}
Write-Host "== dotnet test EasyPDF.slnx -p:Brand=$Brand" -ForegroundColor Cyan
dotnet test "$root/EasyPDF.slnx" -p:Brand=$Brand
if ($LASTEXITCODE -ne 0) {
    throw 'Tests failed, no installer built. Fix them, or build anyway with -NoTests (EASYPDF_NO_TESTS=1).'
}
