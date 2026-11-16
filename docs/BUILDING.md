# Building the app and its installers

Back to the [README](../README.md)

Built with C#, .NET 11 and [Avalonia](https://avaloniaui.net/); PDF rendering and editing use
[PDFium](https://pdfium.googlesource.com/pdfium/) (via PDFiumCore), text recognition RapidOcrNet with ONNX Runtime.
`<App>` stands for the shown name of the built brand (`<app>` in lower case).

## Prerequisites
- [.NET SDK 11](https://dotnet.microsoft.com/download/dotnet/11.0) (11.0.100 or newer, see `global.json`) and Git – on every platform
- Linux packages: Python 3 (no `dpkg` needed)
- macOS disk images: a Mac with the Xcode command line tools (`codesign`, `hdiutil`)

The .NET version is set in two places, because they control different things:

| | `global.json` | `AppTargetFramework` in `Directory.Build.props` |
|---|---|---|
| Sets | **what builds**: the SDK (dotnet CLI, C# compiler, MSBuild) | **what it builds for**: the target framework (`net11.0`) |
| Affects | the build, locally and in CI (`setup-dotnet` reads `global.json`) | the program: which .NET APIs are available and which runtime a publish bundles |
| Without it | the newest installed SDK builds, possibly a preview, and CI may use a different one | required, no project builds without it |

Both are set to .NET 11. Moving to a newer .NET version means changing both.

The version number has four parts, e.g. `1.0.0.97`:
- The first three come from `<VersionPrefix>` in `Directory.Build.props`.
- The fourth is the number of commits, added automatically (`Directory.Build.targets`), so every commit gets a higher number.

Help ▸ About also shows the commit hash: `1.0.0.97 (3fa9c1d2)`. Warnings are treated as errors.
All output goes to `artifacts/` (`publish/<rid>/` for the app, `installer/` for the installers).

**Native AOT (trial):** the profiles `win-x64-aot`, `linux-x64-aot` and `osx-arm64-aot` compile the app to native code
ahead of time instead of ReadyToRun (`dotnet publish src/EasyPDF.App -p:PublishProfile=linux-x64-aot` → `artifacts/publish/<rid>-aot/`).
They need the platform's C toolchain: Visual Studio with "Desktop development with C++" on Windows, `clang` and `zlib1g-dev`
on Linux, Xcode command line tools on macOS. The installers still use the ReadyToRun profiles.
On Windows, `installer\build-aot-windows-on-windows.cmd` (or `installer/windows/build-aot-windows-on-windows.ps1`, same
parameters as `build-windows-on-windows`) checks for the C++ build tools, does a full rebuild with the `win-x64-aot` profile and
writes `artifacts/installer/<App>-<version>-win-x64-aot.zip`, a portable version to compare with the ReadyToRun `-portable.zip`. On Linux, `python3 installer/linux/build_linux.py --aot` packages the `linux-x64-aot` publish output into `<app>_<version>_amd64-aot.deb` and `<App>-<version>-linux-x64-aot.tar.gz` (same package name, so it replaces the ReadyToRun package and vice versa). All libraries in use compile
without trim/AOT warnings; the analyzers are on in both projects, so code that would break under AOT fails the build.

**Brand:** every command builds the brand selected in `Directory.Build.props`; another one with
`-p:Brand=<id>`, the environment variable `EASYPDF_BRAND` or `-Brand <id>` (also `-p:<id>` or just `<id>`) of the installer scripts
([branding/README.md](../branding/README.md)). The installer scripts print the brand at the start
(`== Brand <id> (<App>): -p:Brand=<id>`, `installer/selected-brand.ps1`) and pass `-p:Brand=<id>` to every build
command, which they also print.

## Windows
```powershell
dotnet build EasyPDF.slnx
dotnet test EasyPDF.slnx                                      # UI tests run headless, no window appears
dotnet run --project src/EasyPDF.App -- "C:\path\file.pdf"

# Self-contained app (no .NET needed on the target machine): artifacts\publish\win-x64\<App>.exe (shown name of the brand)
dotnet publish src/EasyPDF.App -p:PublishProfile=win-x64

# MSI installer (publishes first): artifacts\installer\<App>-<version>-win-x64.msi (shown name)
# and the portable version next to it: artifacts\installer\<App>-<version>-win-x64-portable.zip
powershell -ExecutionPolicy Bypass -File installer\windows\build-windows-on-windows.ps1   # or: pwsh installer/windows/build-windows-on-windows.ps1
```
The name of each installer script says where to start it: `build-<target>-on-<system>` (`build-windows-on-windows`, `build-linux-on-windows`, `build-macos-on-macos`). All installer scripts (`installer/*/build-*.ps1`, `installer/build-macos-on-macos.sh`) first delete every `bin/` and `obj/` folder (`installer/clean-build-output.ps1`, `git clean -X`), so each installer is a full rebuild without stale output; close the IDE or a running app if a file is in use. Then they run all tests (`dotnet test EasyPDF.slnx`, `installer/run-tests.ps1`; a few minutes): a failing test stops the build, so an installer only comes from a state whose tests pass. `-NoTests` (or the environment variable `EASYPDF_NO_TESTS=1`, the only way for `build-macos-on-macos.sh`) skips them. After a successful build they delete `bin/`, `obj/` and the publish output (`artifacts/publish`) again, so only the installers in `artifacts/installer` are left (not on CI). `installer\build-windows-on-windows.cmd` and `build-linux-on-windows.cmd` then open that folder in Explorer and close; on an error they wait for a key press.
Only one version of the app is ever installed. On Windows and Linux a newer version replaces the installed one, and an older version is refused (uninstall first to go back). Details:
- **Windows** compares only the first three parts, so any build of 1.0.0 replaces an installed 1.0.0.x, an older build too.
- **Linux** treats a package of the same commit as already installed; `apt install --reinstall ./file.deb` installs it anyway.
WiX is restored as a NuGet package, nothing else has to be installed. The MSI installs per user (no administrator rights, no UAC prompt) to `%LocalAppData%\Programs\<App>` (the shown name, `AppDisplayName` of the brand profile), adds a Start menu entry and the Explorer context menu for PDFs, and registers the app as a PDF program ("Open with", Default apps). Its dialog "Preferences" offers to make it the default program: Windows does not let programs do that themselves, so Finish opens the app's page in Settings ▸ Default apps (one click on "Set default"). On Linux and macOS the app asks once at the first start; Settings ▸ "Open PDF files with …" does it on all systems.
The **portable version** (`-win-x64-portable.zip`) is the same publish output in a folder `<App>-<version>`: unpack anywhere (e.g. a USB stick) and start `<App>.exe`, no installation and no administrator rights. It adds no Start menu entry, no Explorer context menu and no registration as a PDF program; the settings live in `%AppData%\<App>` as with the MSI. To remove it, delete the folder.

## Linux (x64)
Running the app needs fontconfig, X11 and ICU, e.g. on Debian/Ubuntu: `sudo apt install libfontconfig1 libx11-6 libice6 libsm6 libicu74` (the ICU version depends on the distribution), and glibc 2.28 or newer for the text recognition (Debian 10, Ubuntu 20.04 and later).
```bash
dotnet build EasyPDF.slnx
dotnet test EasyPDF.slnx                                      # headless, no display needed
dotnet run --project src/EasyPDF.App -- ~/file.pdf

# Self-contained app: artifacts/publish/linux-x64/<App> (shown name)
dotnet publish src/EasyPDF.App -p:PublishProfile=linux-x64

# .deb (Debian/Ubuntu) and .tar.gz (any distribution): one step incl. publish (meant for Windows, runs with PowerShell 7 on Linux and macOS too)
pwsh installer/linux/build-linux-on-windows.ps1   # or by hand after the publish above: python3 installer/linux/build_linux.py
sudo apt install ./artifacts/installer/<app>_<version>_amd64.deb
```
The packaging script also runs on Windows or macOS (after publishing `linux-x64` there). The `.deb` (package `<app>`: the shown name in lower case) installs to `/opt/<App>`, adds the command `<app>`, the application menu entry and the context menus for Dolphin and Nemo.

## macOS (Apple Silicon, macOS 14 or later)
Intel Macs and macOS 13 are not supported: ONNX Runtime, which the text recognition (OCR) uses, has no Intel build and needs macOS 14.
```bash
dotnet build EasyPDF.slnx
dotnet test EasyPDF.slnx
dotnet run --project src/EasyPDF.App -- ~/file.pdf

# Self-contained app: artifacts/publish/osx-arm64/
dotnet publish src/EasyPDF.App -p:PublishProfile=osx-arm64

# <App>.app (signed ad hoc) in a .dmg: artifacts/installer/<App>-<version>-macos-arm64.dmg (shown name)
bash installer/build-macos-on-macos.sh   # other brand: bash installer/build-macos-on-macos.sh <id>
```
The disk images can only be built on a Mac. Without an Apple Developer ID the app is only ad hoc signed, not notarized, so macOS blocks a downloaded copy. After dragging it to *Applications*, remove the quarantine flag once:
```
xattr -cr /Applications/<App>.app
```
Without Terminal: start it once, close the warning, then System Settings ▸ Privacy & Security ▸ *Open Anyway*. A copy built on the same Mac starts without warning.

## Without local tools: GitHub Actions
The workflow *Installers* (`.github/workflows/installers.yml`) builds the `.msi`, the portable Windows `.zip`, `.deb`/`.tar.gz` and the `.dmg` on GitHub, tests the MSI and `.deb` upgrades, starts the installed app on Linux and the unpacked portable app on Windows and runs the OCR tests on Linux and macOS; all tests run on Windows (in `build-windows-on-windows.ps1`, the other jobs skip them with `EASYPDF_NO_TESTS`), so a failing test stops the Windows installers there. It runs manually (Actions ▸ Installers ▸ Run workflow, or `gh workflow run installers.yml --ref <branch>`); the automatic trigger is commented out in the workflow. *platform* builds all three (default) or only one, *brand* picks the brand profile (empty: the default in `Directory.Build.props`), *aot* (on by default) also builds the Native AOT trial versions of the chosen platforms in jobs of their own (Windows `-win-x64-aot.zip`, Linux `-aot.deb`/`-linux-x64-aot.tar.gz`, each installed or unpacked and started once).

### Installers in the pre-release *latest*
All installers go into the pre-release *latest* (release files do not count against the 0.5 GB of Actions storage of the free plan, so there are no workflow artifacts). A full build creates it anew, a build of a single platform replaces only that platform's files. So there is only ever the newest build and it needs no cleanup. The repository is private: only its members can see and download it.

- Link: [Releases ▸ latest](../../../releases/tag/latest) on GitHub
- Build (Actions ▸ Installers ▸ Run workflow, or):
  ```
  gh workflow run installers.yml --ref <branch>                  # all three
  gh workflow run installers.yml --ref <branch> -f platform=macos # only one: windows, linux or macos
  gh run watch
  ```
- Download into the current folder (`-D <folder>` for another one, `-p "*.dmg"` for only some files):
  ```
  gh release download latest
  ```
- Delete it by hand (release and tag):
  ```
  gh release delete latest --cleanup-tag --yes
  ```

## Development and release repositories
All work happens in the development repository, on `main` only; there are no brand branches. Each published brand has
its own release repository: a clone next to the development repository, in a folder named after the brand's shown
name (the brand's `README.md` in `branding/<id>/` names it). A release repository holds the files of `main` with these
differences:
- `<RepositoryUrl>` in `Directory.Build.props` is its own address.
- If its brand is not `easypdf`, that brand is the default: `<Brand>` in `Directory.Build.props` and the places that
  state the default (`CLAUDE.md`, `README.md`, `README_DE.md`, `branding/README.md`, `.claude/skills/branding/SKILL.md`).

Nothing else is edited in a release repository, and it is never force pushed: every commit of `main` arrives there as
an ordinary commit, so the history shows each change. After a commit on `main` (build and tests green, pushed), in
every release repository:
```
git fetch <path of the development repository> main
git cherry-pick -x <last>..FETCH_HEAD      # <last>: see below
git push origin main
```
- `<last>` is the commit of `main` that was carried over last: the hash in the line `(cherry picked from commit …)`
  of the newest commit (`git log -1`).
- On a conflict in one of the files above, keep the release repository's address and default brand and take
  everything else from `main`.
- Check before the push: `git diff --stat FETCH_HEAD HEAD` lists only the files above.

The build number is the number of commits, so the same change has a lower one in a release repository.

## Demo videos
`branding/<id>/make-presentation-videos.cmd` records the introduction in German and English for that brand
(`tools/DemoVideo`, drives the real app headless; ffmpeg is downloaded into a temp folder for the run) into
`branding/<id>/videos/` (`introduction.de.mp4`, `introduction.en.mp4`); they are committed and shipped with the app
(video button in the header).
