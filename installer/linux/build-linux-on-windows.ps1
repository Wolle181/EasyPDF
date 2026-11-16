# Builds the Linux packages: artifacts/installer/<package>_<version>_amd64.deb (package = AppDisplayName in lower case) and <AppDisplayName>-<version>-linux-x64.tar.gz
# Runs on Windows, Linux and macOS (needs the .NET SDK and Python 3; dpkg is not required).
# Always pulls the newest sources first (git pull --ff-only; -NoPull skips it) and deletes all bin/obj
# (full rebuild, no stale output from earlier builds). Then it runs all tests (installer/run-tests.ps1): a failing test
# stops the build; -NoTests (or EASYPDF_NO_TESTS=1) skips them. Afterwards it deletes bin/obj and the publish output again,
# so only artifacts/installer is left.
# -Brand <id> builds that brand profile (branding/<id>/); also written -p:<id>, -p:Brand=<id> (like msbuild) or just <id>.
# Without it EASYPDF_BRAND or the default (Directory.Build.props). Unknown parameters stop the script, so a typo never
# builds the default brand by mistake.
[CmdletBinding()]
param([Parameter(Position = 0)] [Alias('p')] [string]$Brand, [switch]$NoPull, [switch]$NoTests)
$ErrorActionPreference = 'Stop'
$installer = Split-Path -Parent $PSScriptRoot   # the shared helper scripts
$root = Split-Path -Parent $installer
if ($Brand) { $env:EASYPDF_BRAND = $Brand -replace '^Brand=', '' }   # seen by every dotnet/python call below, also after a restart
if ($NoTests) { $env:EASYPDF_NO_TESTS = '1' }                         # likewise

# New commits may have changed this script too: then its new version runs
if (-not $NoPull -and (& "$installer/update-sources.ps1")) { & $PSCommandPath -NoPull; exit $LASTEXITCODE }

# On Windows `python3` can be a Microsoft Store stub, so pick the first one that really runs
function Test-Python($name) {
    try { return ((& $name -c 'print(1)' 2>$null) -eq '1') } catch { return $false }
}
$python = @('python3', 'python') | Where-Object { Test-Python $_ } | Select-Object -First 1
if (-not $python) { throw 'Python 3 not found (python3 / python).' }

# Shown with the -p parameter the publish gets; build_linux.py reads it from EASYPDF_BRAND
$brand = & "$installer/selected-brand.ps1"

& "$installer/clean-build-output.ps1"
& "$installer/run-tests.ps1" -Brand $brand

Remove-Item -Recurse -Force "$root/artifacts/publish/linux-x64" -ErrorAction SilentlyContinue
Write-Host "== dotnet publish src/EasyPDF.App -p:PublishProfile=linux-x64 -p:Brand=$brand" -ForegroundColor Cyan
dotnet publish "$root/src/EasyPDF.App" -p:PublishProfile=linux-x64 -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "== $python installer/linux/build_linux.py   (EASYPDF_BRAND=$brand)" -ForegroundColor Cyan
& $python "$root/installer/linux/build_linux.py"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# Only the installers stay
if (-not $env:CI) {
    try { & "$installer/clean-build-output.ps1" -Intermediate } catch { Write-Warning "Packages built, but cleaning up failed: $_" }
}
Get-ChildItem "$root/artifacts/installer/*linux*", "$root/artifacts/installer/*.deb" -ErrorAction SilentlyContinue | Sort-Object FullName -Unique
