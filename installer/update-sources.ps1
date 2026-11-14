# Brings the sources up to date before a build: `git pull --ff-only` on the current branch (never a merge commit).
# Skipped on CI servers (they check out exactly the commit to build) and with a detached HEAD.
# Returns $true if new commits came in: the calling build script then starts again, so its own new version runs.
# Called by every installer/*/build-*.ps1 script (unless started with -NoPull).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ($env:CI) {
    Write-Host '== CI: no git pull'
    return $false
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git not found: needed to update the sources (or start with -NoPull).' }

$branch = git -C $root symbolic-ref --short -q HEAD
if (-not $branch) {
    Write-Host '== Detached HEAD: no git pull'
    return $false
}
$before = git -C $root rev-parse HEAD
Write-Host "== git pull ($branch)"
git -C $root pull --ff-only | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw 'git pull failed (local changes in the way, branch diverged or no upstream). Fix it, or build the current state with -NoPull.'
}
return ($before -ne (git -C $root rev-parse HEAD))
