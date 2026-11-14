# The brand profile the installer scripts build: -Brand of the calling script (already in EASYPDF_BRAND), otherwise
# EASYPDF_BRAND or the default in Directory.Build.props. Prints it with the -p parameter the build commands get, so the
# console shows which brand is being built, and fixes it in EASYPDF_BRAND for tools without -p (build_linux.py).
# Returns the brand id. Fails early if the brand profile does not exist.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$json = dotnet msbuild "$root/src/EasyPDF.App" -getProperty:Brand -getProperty:AppDisplayName | Out-String
if ($LASTEXITCODE -ne 0) { throw "Cannot read the brand profile (EASYPDF_BRAND=$env:EASYPDF_BRAND):`n$json" }
$props = ($json | ConvertFrom-Json).Properties
Write-Host "== Brand $($props.Brand) ($($props.AppDisplayName)): -p:Brand=$($props.Brand)" -ForegroundColor Cyan
$env:EASYPDF_BRAND = $props.Brand
$props.Brand
