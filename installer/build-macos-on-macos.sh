#!/usr/bin/env bash
# Builds the macOS disk image with the app compiled by Native AOT (run on a Mac; needs the .NET SDK, the Xcode command
# line tools for the native linker, codesign and hdiutil):
#   artifacts/installer/<AppDisplayName>-<version>-macos-arm64.dmg
# Apple Silicon and macOS 14 or later only: ONNX Runtime (used by OCR) has no Intel build and needs macOS 14.
# Usage: bash installer/build-macos-on-macos.sh [<id> | -p:<id> | -p:Brand=<id> | -Brand <id>]
#   brand profile branding/<id>/ like the other installer scripts; without it EASYPDF_BRAND or the default
#   (Directory.Build.props). Unknown parameters stop the script, so a typo never builds the default brand by mistake.
# Pulls the newest sources first (git pull --ff-only), except on CI, with a detached HEAD or with EASYPDF_NO_PULL=1.
# Runs all tests after cleaning (like installer/run-tests.ps1): a failing test stops the build; EASYPDF_NO_TESTS=1 skips them.
# Deletes all bin/obj before the build (full rebuild) and, except on CI, bin/obj and the intermediate output
# (artifacts/publish, artifacts/macos) after it, so only artifacts/installer is left.
#
# The app is signed ad hoc only (required to run on Apple Silicon at all). Without an Apple Developer ID and
# notarization, Gatekeeper asks on first start: right-click the app > Open, or System Settings > Privacy & Security.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

case "${1:-}" in
  "") ;;
  -Brand|--brand) EASYPDF_BRAND="${2:?-Brand needs a brand id}"; shift 2 ;;
  -p:*) EASYPDF_BRAND="${1#-p:}"; EASYPDF_BRAND="${EASYPDF_BRAND#Brand=}"; shift ;;
  -*) echo "Unknown parameter: $1" >&2; exit 1 ;;
  *) EASYPDF_BRAND="$1"; shift ;;
esac
[ $# -eq 0 ] || { echo "Unexpected parameters: $*" >&2; exit 1; }
[ -z "${EASYPDF_BRAND:-}" ] || export EASYPDF_BRAND   # also seen after a restart below

# bin/obj of every project (git clean -X: only ignored files), like installer/clean-build-output.ps1
clean_build_output() {
  dotnet build-server shutdown >/dev/null 2>&1 || true
  echo "== Cleaning build output (git clean -fdX src tests installer)"
  git -C "$ROOT" clean -fdX -- src tests installer
}

# Newest sources first; new commits may have changed this script too, then its new version runs
if [ -z "${CI:-}" ] && [ -z "${EASYPDF_NO_PULL:-}" ] && git -C "$ROOT" symbolic-ref -q HEAD >/dev/null; then
  BEFORE="$(git -C "$ROOT" rev-parse HEAD)"
  echo "== git pull"
  git -C "$ROOT" pull --ff-only || { echo "git pull failed; fix it or run with EASYPDF_NO_PULL=1" >&2; exit 1; }
  if [ "$BEFORE" != "$(git -C "$ROOT" rev-parse HEAD)" ]; then
    EASYPDF_NO_PULL=1 exec bash "$0"
  fi
fi
# Brand profile (EASYPDF_BRAND or the default in Directory.Build.props), shown with the -p parameter the publish gets
BRAND="$(dotnet msbuild "$ROOT/src/EasyPDF.App" -getProperty:Brand | tr -d '\r\n')"
[ -n "$BRAND" ] || { echo "Cannot read the brand profile (EASYPDF_BRAND=${EASYPDF_BRAND:-})" >&2; exit 1; }
export EASYPDF_BRAND="$BRAND"
clean_build_output
if [ -n "${EASYPDF_NO_TESTS:-}" ]; then
  echo "== Tests skipped (EASYPDF_NO_TESTS)"
else
  echo "== dotnet test EasyPDF.slnx -p:Brand=$BRAND"
  dotnet test "$ROOT/EasyPDF.slnx" -p:Brand="$BRAND" ||
    { echo "Tests failed, no installer built. Fix them, or build anyway with EASYPDF_NO_TESTS=1." >&2; exit 1; }
fi
# 1.0.0.<number of commits> (Directory.Build.targets). macOS shows "Version 1.0.0 (97)": CFBundleShortVersionString
# allows three numbers only, the build number goes into CFBundleVersion.
VERSION="$(dotnet msbuild "$ROOT/src/EasyPDF.App" -getProperty:Version -t:SetVersionFromGit | tr -d '[:space:]')"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Unexpected version: $VERSION" >&2; exit 1; }
# From the brand profile (branding/<Brand>/Branding.props, brand chosen by EASYPDF_BRAND):
# shown name for app bundle, executable, menu bar, Dock, disk image
APPNAME="$(dotnet msbuild "$ROOT/src/EasyPDF.App" -getProperty:AppDisplayName | tr -d '\r\n')"
[ -n "$APPNAME" ] || { echo "AppDisplayName missing in the brand profile" >&2; exit 1; }
# Copyright for Finder's info window, bundle id, icons
COPYRIGHT="$(dotnet msbuild "$ROOT/src/EasyPDF.App" -getProperty:Copyright | tr -d '\r\n')"
BUNDLEID="$(dotnet msbuild "$ROOT/src/EasyPDF.App" -getProperty:MacBundleId | tr -d '\r\n')"
BRANDDIR="$(dotnet msbuild "$ROOT/src/EasyPDF.App" -getProperty:BrandDir | tr -d '\r\n')"
echo "== Brand $BRAND ($APPNAME): -p:Brand=$BRAND"
SHORT_VERSION="${VERSION%.*}"
BUILD="${VERSION##*.}"
mkdir -p "$ROOT/artifacts/installer"

A=arm64
RID="osx-$A"
PROFILE="$RID"
echo "== $PROFILE $VERSION"
rm -rf "$ROOT/artifacts/publish/$PROFILE"
echo "== dotnet publish src/EasyPDF.App -p:PublishProfile=$PROFILE -p:Version=$VERSION -p:Brand=$BRAND"
dotnet publish "$ROOT/src/EasyPDF.App" -p:PublishProfile="$PROFILE" -p:Version="$VERSION" -p:Brand="$BRAND"
# Debug symbols of the native program (.dSYM, .pdb) are not needed to run it
find "$ROOT/artifacts/publish/$PROFILE" \( -name '*.dSYM' -o -name '*.pdb' \) -prune -exec rm -rf {} +

WORK="$ROOT/artifacts/macos/$A"
APP="$WORK/stage/$APPNAME.app"
rm -rf "$WORK"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$ROOT/artifacts/publish/$PROFILE/." "$APP/Contents/MacOS/"
cp "$BRANDDIR/icons/app.icns" "$APP/Contents/Resources/AppIcon.icns"
sed -e "s/@VERSION@/$SHORT_VERSION/g" -e "s/@BUILD@/$BUILD/g" -e "s/@APPNAME@/$APPNAME/g" -e "s/@COPYRIGHT@/$COPYRIGHT/g" -e "s/@BUNDLEID@/$BUNDLEID/g" "$ROOT/installer/macos/Info.plist" > "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/$APPNAME"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

# Disk image with the app and a link to /Applications for drag-and-drop installation
ln -s /Applications "$WORK/stage/Applications"
DMG="$ROOT/artifacts/installer/$APPNAME-$VERSION-macos-$A.dmg"
rm -f "$DMG"
for attempt in 1 2 3; do   # hdiutil occasionally fails with "Resource busy" on CI machines
  if hdiutil create -volname "$APPNAME" -srcfolder "$WORK/stage" -ov -format UDZO "$DMG"; then break; fi
  [ "$attempt" = 3 ] && exit 1
  sleep 5
done
# Only the installers stay. Not on CI: fresh machine each run
if [ -z "${CI:-}" ]; then
  clean_build_output
  echo "== Cleaning intermediate output (artifacts/publish, artifacts/macos)"
  rm -rf "$ROOT/artifacts/publish" "$ROOT/artifacts/macos"
fi
echo "$DMG"
