# Builds the container image of the web version (Dockerfile: server + browser program) and pushes it to the registry
# (default: the private registry on the NAS, 10.32.15.188:5500) as <name>:<version> and <name>:latest, where <name> is the
# brand's shown name in lower case plus "-web". Watchtower on the host then rolls out :latest.
# Runs on Linux and Windows with PowerShell 7 (pwsh) and Podman (Docker works too); starters: BuildContainer.sh,
# BuildContainer.cmd in the repository root.
#
# Gates, in this order (each one stops the script):
#   0. git pull --ff-only (installer/update-sources.ps1; -NoPull skips it); uncommitted changes stop the script
#      (the image is tagged with the version of the commit, so it must be exactly that commit; -AllowDirty overrides)
#   1. no vulnerable NuGet package (dotnet list package --vulnerable --include-transitive)
#   2. all tests (installer/run-tests.ps1; -NoTests skips them); the image build runs the core and server tests again
#   3. image build (the whole build runs inside the container, see Dockerfile)
#   4. push (-NoPush: build only, nothing pushed - a trial build never reaches :latest)
# -Brand <id> builds that brand profile (also -p:<id>, -p:Brand=<id> or just <id>), otherwise EASYPDF_BRAND or the default.
[CmdletBinding()]
param([Parameter(Position = 0)] [Alias('p')] [string]$Brand, [switch]$NoPull, [switch]$NoTests, [switch]$NoPush,
      [switch]$AllowDirty, [string]$Registry = '10.32.15.188:5500')
$ErrorActionPreference = 'Stop'
$installer = Split-Path -Parent $PSScriptRoot
$root = Split-Path -Parent $installer
if ($Brand) { $env:EASYPDF_BRAND = $Brand -replace '^Brand=', '' }
if ($NoTests) { $env:EASYPDF_NO_TESTS = '1' }

# 0. newest sources (a new version of this script then runs itself)
if (-not $NoPull -and (& "$installer/update-sources.ps1")) {
    & $PSCommandPath -NoPull -NoPush:$NoPush -AllowDirty:$AllowDirty -Registry $Registry
    exit $LASTEXITCODE
}
$dirty = git -C $root status --porcelain --untracked-files=no
if ($dirty -and -not $AllowDirty) {
    throw "Uncommitted changes - commit them first (the image carries the version of the commit), or start with -AllowDirty:`n$($dirty -join "`n")"
}

$tool = @('podman', 'docker') | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $tool) { throw 'Neither podman nor docker found.' }

$brand = & "$installer/selected-brand.ps1"
$appName = (dotnet msbuild "$root/src/EasyPDF.App" -getProperty:AppDisplayName -p:Brand=$brand | Out-String).Trim()
$version = (dotnet msbuild "$root/src/EasyPDF.App" -getProperty:Version -t:SetVersionFromGit -p:Brand=$brand | Out-String).Trim()
$commit = (git -C $root rev-parse --short=8 HEAD | Out-String).Trim()
$image = ($appName.ToLowerInvariant() -replace '[^a-z0-9._-]', '') + '-web'
Write-Host "== Image $image`:$version (commit $commit, brand $brand)" -ForegroundColor Cyan

# 1. vulnerable packages: the command exits with 0 even when it finds some, so its output is checked (lines with ">")
Write-Host '== Checking NuGet packages for known vulnerabilities' -ForegroundColor Cyan
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$report = dotnet list "$root/EasyPDF.slnx" package --vulnerable --include-transitive 2>&1 | Out-String   # the brand comes from EASYPDF_BRAND
$code = $LASTEXITCODE
$ErrorActionPreference = $previous
if ($code -ne 0) { throw "Vulnerability check could not run:`n$report" }
$findings = @($report -split "`r?`n" | Where-Object { $_ -match '^\s*>\s+\S+' })
if ($findings.Count -gt 0) {
    Write-Host $report
    throw "$($findings.Count) vulnerable NuGet package(s) found - update them before building the image."
}
Write-Host 'No vulnerable packages.'

# 2. all tests (desktop UI included: the shared user interface is the browser program's too)
& "$installer/run-tests.ps1" -Brand $brand

# 3. image build
if ($IsWindows -and $tool -eq 'podman') {
    podman machine start 2>$null | Out-Null   # already running is fine
}
$local = "$image`:$version"
Write-Host "== $tool build -t $local" -ForegroundColor Cyan
& $tool build -t $local --build-arg "BRAND=$brand" --build-arg "VERSION=$version" --build-arg "COMMIT=$commit" -f "$root/Dockerfile" $root
if ($LASTEXITCODE -ne 0) { throw "$tool build failed." }

if ($NoPush) {
    Write-Host "== Trial build done: $local exists locally only (not pushed)." -ForegroundColor Green
    exit 0
}

# 4. push with the version and as latest
$tlsOption = if ($tool -eq 'podman') { @('--tls-verify=false') } else { @() }
foreach ($tag in @($version, 'latest')) {
    $remote = "$Registry/$image`:$tag"
    & $tool tag $local $remote
    if ($LASTEXITCODE -ne 0) { throw "$tool tag failed." }
    Write-Host "== $tool push $remote" -ForegroundColor Cyan
    & $tool push @tlsOption $remote
    if ($LASTEXITCODE -ne 0) { throw "$tool push failed." }
}
Write-Host "== Pushed $Registry/$image`:$version and :latest" -ForegroundColor Green
try {
    $tags = Invoke-RestMethod -Uri "http://$Registry/v2/$image/tags/list" -TimeoutSec 10
    Write-Host "Tags in the registry: $($tags.tags -join ', ')"
}
catch {
    Write-Host "(Could not list the tags: $($_.Exception.Message))"
}
