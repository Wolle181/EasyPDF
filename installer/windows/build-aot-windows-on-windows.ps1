# Trial: builds the app with Native AOT instead of ReadyToRun, as a portable version:
#   artifacts/installer/<AppDisplayName>-<version>-win-x64-aot.zip  unpack and run (folder <AppDisplayName>-<version>-aot)
# No MSI: the installers keep using ReadyToRun (build-windows-on-windows). Next to that script's -portable.zip, the two
# versions can be compared directly (start time, size, behaviour).
# Needs the .NET SDK and the C++ build tools for the native linker: Visual Studio (or the Build Tools for Visual Studio)
# with the workload "Desktop development with C++".
# Like the other build scripts it pulls the newest sources first (git pull --ff-only; -NoPull skips it), deletes all
# bin/obj before (full rebuild) and afterwards, so only artifacts/installer is left. In between it runs all tests
# (installer/run-tests.ps1): a failing test stops the build; -NoTests (or EASYPDF_NO_TESTS=1) skips them.
# -Brand <id> builds that brand profile (branding/<id>/); also written -p:<id>, -p:Brand=<id> (like msbuild) or just <id>.
# Without it EASYPDF_BRAND or the default (Directory.Build.props). Unknown parameters stop the script.
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

# 1.0.0.<number of commits> (Directory.Build.targets)
$version = dotnet msbuild "$root/src/EasyPDF.App" -getProperty:Version -t:SetVersionFromGit -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Remove-Item -Recurse -Force "$root/artifacts/publish/win-x64-aot" -ErrorAction SilentlyContinue
Write-Host "== dotnet publish src/EasyPDF.App -p:PublishProfile=win-x64-aot -p:Version=$version -p:Brand=$brand" -ForegroundColor Cyan
Write-Host '   (compiling the whole program to native code takes a few minutes)'
$watch = [Diagnostics.Stopwatch]::StartNew()
dotnet publish "$root/src/EasyPDF.App" -p:PublishProfile=win-x64-aot -p:Version=$version -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host ("== Native AOT build took {0:m\:ss} min" -f $watch.Elapsed) -ForegroundColor Cyan

# Portable version: the publish output in a folder <AppDisplayName>-<version>-aot, zipped
$appName = dotnet msbuild "$root/src/EasyPDF.App" -getProperty:AppDisplayName -p:Brand=$brand
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$publishDir = (Resolve-Path "$root/artifacts/publish/win-x64-aot").Path
$size = (Get-ChildItem -Recurse -File $publishDir | Measure-Object -Property Length -Sum).Sum
Write-Host ("== {0}.exe {1:N1} MB, whole folder {2:N1} MB" -f $appName, ((Get-Item "$publishDir/$appName.exe").Length / 1MB), ($size / 1MB)) -ForegroundColor Cyan
New-Item -ItemType Directory -Force "$root/artifacts/installer" | Out-Null
$zip = Join-Path (Resolve-Path "$root/artifacts/installer").Path "$appName-$version-win-x64-aot.zip"
Write-Host "== $zip" -ForegroundColor Cyan
Remove-Item $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -Recurse -File $publishDir) {
        # Debug symbols of the native program (.pdb) are not needed to run it
        if ($file.Extension -eq '.pdb') { continue }
        $entry = "$appName-$version-aot/" + $file.FullName.Substring($publishDir.Length).TrimStart('\', '/').Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }

# Only the zip stays
try { & "$installer/clean-build-output.ps1" -Intermediate } catch { Write-Warning "Built, but cleaning up failed: $_" }
Get-ChildItem $zip
