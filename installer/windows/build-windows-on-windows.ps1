# Builds the Windows installer and the portable version, both with the app compiled by Native AOT:
#   artifacts/installer/<AppDisplayName>-<version>-Windows-Installer.msi  per-user installer
#   artifacts/installer/<AppDisplayName>-<version>-Windows-portable.zip   unpack and run (folder <AppDisplayName>-<version>)
# Needs the .NET SDK (WiX comes as NuGet package) and the C++ build tools for the native linker: Visual Studio (or the
# Build Tools for Visual Studio) with the workload "Desktop development with C++".
# Always pulls the newest sources first (git pull --ff-only; -NoPull skips it) and deletes all bin/obj
# (full rebuild, no stale output from earlier builds). Then it runs all tests (installer/run-tests.ps1): a failing test
# stops the build; -NoTests (or EASYPDF_NO_TESTS=1) skips them. Afterwards it deletes bin/obj and the publish output again,
# so only artifacts/installer is left (not on CI: the upgrade test there rebuilds the MSI from artifacts/publish).
# -Brand <id> builds that brand profile (branding/<id>/); also written -p:<id>, -p:Brand=<id> (like msbuild) or just <id>.
# Without it EASYPDF_BRAND or the default (Directory.Build.props). Unknown parameters stop the script, so a typo never
# builds the default brand by mistake.
[CmdletBinding()]
param([Parameter(Position = 0)] [Alias('p')] [string]$Brand, [switch]$NoPull, [switch]$NoTests)
$ErrorActionPreference = 'Stop'
$installer = Split-Path -Parent $PSScriptRoot   # the shared helper scripts
$root = Split-Path -Parent $installer
if ($Brand) { $env:EASYPDF_BRAND = $Brand -replace '^Brand=', '' }   # seen by every dotnet call below, also after a restart
if ($NoTests) { $env:EASYPDF_NO_TESTS = '1' }                         # likewise

# New commits may have changed this script too: then its new version runs
if (-not $NoPull -and (& "$installer/update-sources.ps1")) { & $PSCommandPath -NoPull; exit $LASTEXITCODE }

# Native AOT links with the MSVC linker: stop early with a clear message instead of failing after minutes of compiling
$vswhere = if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe' }
$vcTools = if ($vswhere -and (Test-Path $vswhere)) {
    & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $vcTools) {
    throw ('Native AOT needs the C++ build tools: install Visual Studio or the "Build Tools for Visual Studio" ' +
        '(https://visualstudio.microsoft.com/downloads/) with the workload "Desktop development with C++", then run this script again.')
}
Write-Host "== C++ build tools: $vcTools" -ForegroundColor Cyan

# Shown with the -p parameter every build command below gets
$brand = & "$installer/selected-brand.ps1"

& "$installer/clean-build-output.ps1"
& "$installer/run-tests.ps1" -Brand $brand

# 1.0.0.<number of commits> (Directory.Build.targets); app and MSI get the same
$version = dotnet msbuild "$root/src/EasyPDF.App" -getProperty:Version -t:SetVersionFromGit -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Remove-Item -Recurse -Force "$root/artifacts/publish/win-x64" -ErrorAction SilentlyContinue
Write-Host "== dotnet publish src/EasyPDF.App -p:PublishProfile=win-x64 -p:Version=$version -p:Brand=$brand" -ForegroundColor Cyan
Write-Host '   (compiling the whole program to native code takes a few minutes)'
$watch = [Diagnostics.Stopwatch]::StartNew()
dotnet publish "$root/src/EasyPDF.App" -p:PublishProfile=win-x64 -p:Version=$version -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host ("== Native AOT build took {0:m\:ss} min" -f $watch.Elapsed) -ForegroundColor Cyan

# Debug symbols of the native program (.pdb, large) are not needed to run it: neither in the .zip nor in the MSI
Get-ChildItem -Recurse -File "$root/artifacts/publish/win-x64" -Filter *.pdb | Remove-Item

# Portable version: the publish output in a folder <AppDisplayName>-<version>, zipped (no installer, no registry entries)
$appName = dotnet msbuild "$root/src/EasyPDF.App" -getProperty:AppDisplayName -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$publishDir = (Resolve-Path "$root/artifacts/publish/win-x64").Path
$size = (Get-ChildItem -Recurse -File $publishDir | Measure-Object -Property Length -Sum).Sum
Write-Host ("== {0}.exe {1:N1} MB, whole folder {2:N1} MB" -f $appName, ((Get-Item "$publishDir/$appName.exe").Length / 1MB), ($size / 1MB)) -ForegroundColor Cyan
New-Item -ItemType Directory -Force "$root/artifacts/installer" | Out-Null
$zip = Join-Path (Resolve-Path "$root/artifacts/installer").Path "$appName-$version-Windows-portable.zip"
Write-Host "== $zip" -ForegroundColor Cyan
Remove-Item $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -Recurse -File $publishDir) {
        $entry = "$appName-$version/" + $file.FullName.Substring($publishDir.Length).TrimStart('\', '/').Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }

Write-Host "== dotnet build installer/windows/EasyPDF.Installer.wixproj -c Release -p:Version=$version -p:Brand=$brand" -ForegroundColor Cyan
dotnet build "$root/installer/windows/EasyPDF.Installer.wixproj" -c Release -p:Version=$version -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Only the installers stay. Not on CI: the upgrade test there rebuilds the MSI from artifacts/publish
if (-not $env:CI) {
    try { & "$installer/clean-build-output.ps1" -Intermediate } catch { Write-Warning "Installer built, but cleaning up failed: $_" }
}
Get-ChildItem "$root/artifacts/installer/*-Windows-Installer.msi", $zip
