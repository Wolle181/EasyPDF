# Builds the Windows installer: artifacts/installer/<AppDisplayName>-<version>-win-x64.msi
# Needs only the .NET SDK (WiX comes as NuGet package).
# Always pulls the newest sources first (git pull --ff-only; -NoPull skips it) and deletes all bin/obj
# (full rebuild, no stale output from earlier builds). Afterwards it deletes bin/obj and the publish output again,
# so only artifacts/installer is left.
# -Brand <id> builds that brand profile (branding/<id>/); also written -p:<id>, -p:Brand=<id> (like msbuild) or just <id>.
# Without it EASYPDF_BRAND or the default (Directory.Build.props). Unknown parameters stop the script, so a typo never
# builds the default brand by mistake.
[CmdletBinding()]
param([Parameter(Position = 0)] [Alias('p')] [string]$Brand, [switch]$NoPull)
$ErrorActionPreference = 'Stop'
$installer = Split-Path -Parent $PSScriptRoot   # the shared helper scripts
$root = Split-Path -Parent $installer
if ($Brand) { $env:EASYPDF_BRAND = $Brand -replace '^Brand=', '' }   # seen by every dotnet/python call below, also after a restart

# New commits may have changed this script too: then its new version runs
if (-not $NoPull -and (& "$installer/update-sources.ps1")) { & $PSCommandPath -NoPull; exit $LASTEXITCODE }

# Shown with the -p parameter every build command below gets
$brand = & "$installer/selected-brand.ps1"

& "$installer/clean-build-output.ps1"

# 1.0.0.<number of commits> (Directory.Build.targets); app and MSI get the same
$version = dotnet msbuild "$root/src/EasyPDF.App" -getProperty:Version -t:SetVersionFromGit -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Remove-Item -Recurse -Force "$root/artifacts/publish/win-x64" -ErrorAction SilentlyContinue
Write-Host "== dotnet publish src/EasyPDF.App -p:PublishProfile=win-x64 -p:Version=$version -p:Brand=$brand" -ForegroundColor Cyan
dotnet publish "$root/src/EasyPDF.App" -p:PublishProfile=win-x64 -p:Version=$version -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "== dotnet build installer/windows/EasyPDF.Installer.wixproj -c Release -p:Version=$version -p:Brand=$brand" -ForegroundColor Cyan
dotnet build "$root/installer/windows/EasyPDF.Installer.wixproj" -c Release -p:Version=$version -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# Only the installers stay. Not on CI: the upgrade test there rebuilds the MSI from artifacts/publish
if (-not $env:CI) {
    try { & "$installer/clean-build-output.ps1" -Intermediate } catch { Write-Warning "Installer built, but cleaning up failed: $_" }
}
Get-ChildItem "$root/artifacts/installer/*.msi"
