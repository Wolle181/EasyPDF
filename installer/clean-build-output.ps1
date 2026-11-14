# Deletes all build output (bin/ and obj/ of every project, incl. the installer project) so the next build starts from scratch.
# Uses `git clean -X`: only files ignored by .gitignore are removed, tracked files and new, not yet added sources stay.
# artifacts/installer (finished installers) is left alone. Called by every installer/*/build-*.ps1 script, can also run on its own.
# -Intermediate also removes the intermediate output of the installer builds (artifacts/publish, artifacts/macos):
# the build scripts pass it after a successful build, so only the installers are left.
param([switch]$Intermediate)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git not found: -Clean needs git.' }

# Build servers (MSBuild nodes, compiler server) keep files in obj/ open, which would stop the clean on Windows
dotnet build-server shutdown | Out-Null

Write-Host '== Cleaning build output (git clean -fdX src tests installer)'
git -C $root clean -fdX -- src tests installer
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Intermediate) {
    Write-Host '== Cleaning intermediate output (artifacts/publish, artifacts/macos)'
    Remove-Item -Recurse -Force "$root/artifacts/publish", "$root/artifacts/macos" -ErrorAction SilentlyContinue
}

# git only warns about files it cannot delete (e.g. held open by a running app or the IDE)
$left = @(Get-ChildItem "$root/src", "$root/tests", "$root/installer" -Recurse -Directory -Include bin, obj -ErrorAction SilentlyContinue)
if ($Intermediate) { $left += @(Get-Item "$root/artifacts/publish", "$root/artifacts/macos" -ErrorAction SilentlyContinue) }
if ($left) { throw "Could not remove all build output (in use?): $($left.FullName -join ', ')" }
