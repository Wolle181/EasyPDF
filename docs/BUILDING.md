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

**Native AOT:** the publish profiles `win-x64`, `linux-x64` and `osx-arm64` compile the whole app to native code ahead of time
(fast start, no JIT); every installer and the portable Windows version are built this way. Native AOT compiles only for the system
it runs on, so each platform's installer is built on that platform, and it needs the platform's C toolchain: Visual Studio (or the
Build Tools for Visual Studio) with "Desktop development with C++" on Windows, `clang` and `zlib1g-dev` on Linux, the Xcode
command line tools on macOS. Compiling takes a few minutes. The installer scripts delete the native debug symbols (`.pdb`,
`.dbg`, `.dSYM`) before packaging. All libraries in use compile without trim/AOT warnings; the analyzers are on in both projects,
so code that would break under AOT fails the build.

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

# Self-contained native app (no .NET needed on the target machine; needs the C++ build tools): artifacts\publish\win-x64\<App>.exe (shown name of the brand)
dotnet publish src/EasyPDF.App -p:PublishProfile=win-x64

# MSI installer (publishes first): artifacts\installer\<App>-<version>-Windows-Installer.msi (shown name)
# and the portable version next to it: artifacts\installer\<App>-<version>-Windows-portable.zip
powershell -ExecutionPolicy Bypass -File installer\windows\build-windows-on-windows.ps1   # or: pwsh installer/windows/build-windows-on-windows.ps1
```
The name of each installer script says where to start it: `build-<target>-on-<system>` (`build-windows-on-windows`, `build-linux-on-linux`, `build-macos-on-macos`). `build-windows-on-windows` checks for the C++ build tools first and stops with a hint if they are missing. All installer scripts (`installer/*/build-*.ps1`, `installer/build-macos-on-macos.sh`) first delete every `bin/` and `obj/` folder (`installer/clean-build-output.ps1`, `git clean -X`), so each installer is a full rebuild without stale output; close the IDE or a running app if a file is in use. Then they run all tests (`dotnet test EasyPDF.slnx`, `installer/run-tests.ps1`; a few minutes): a failing test stops the build, so an installer only comes from a state whose tests pass. `-NoTests` (or the environment variable `EASYPDF_NO_TESTS=1`, the only way for `build-macos-on-macos.sh`) skips them. After a successful build they delete `bin/`, `obj/` and the publish output (`artifacts/publish`) again, so only the installers in `artifacts/installer` are left (not on CI). `installer\build-windows-on-windows.cmd` then opens that folder in Explorer and closes; on an error it waits for a key press.
Only one version of the app is ever installed. On Windows and Linux a newer version replaces the installed one, and an older version is refused (uninstall first to go back). Details:
- **Windows** compares only the first three parts, so any build of 1.0.0 replaces an installed 1.0.0.x, an older build too.
- **Linux** treats a package of the same commit as already installed; `apt install --reinstall ./file.deb` installs it anyway.
WiX is restored as a NuGet package, nothing else has to be installed. The MSI installs per user (no administrator rights, no UAC prompt) to `%LocalAppData%\Programs\<App>` (the shown name, `AppDisplayName` of the brand profile), adds a Start menu entry and the Explorer context menu for PDFs, and registers the app as a PDF program ("Open with", Default apps). Its dialog "Preferences" offers to make it the default program: Windows does not let programs do that themselves, so Finish opens the app's page in Settings ▸ Default apps (one click on "Set default"). On Linux and macOS the app asks once at the first start; Settings ▸ "Open PDF files with …" does it on all systems.
The **portable version** (`-Windows-portable.zip`) is the same publish output in a folder `<App>-<version>`: unpack anywhere (e.g. a USB stick) and start `<App>.exe`, no installation and no administrator rights. It adds no Start menu entry, no Explorer context menu and no registration as a PDF program; the settings live in `%AppData%\<App>` as with the MSI. To remove it, delete the folder.

## Linux (x64)
Running the app needs fontconfig, X11 and ICU, e.g. on Debian/Ubuntu: `sudo apt install libfontconfig1 libx11-6 libice6 libsm6 libicu74` (the ICU version depends on the distribution), and at least the glibc of the system that built it: the CI builds the packages on Ubuntu 24.04 (glibc 2.39: Debian 13, Ubuntu 24.04, Mint 22 and later; Native AOT links against the build system's glibc).
```bash
dotnet build EasyPDF.slnx
dotnet test EasyPDF.slnx                                      # headless, no display needed
dotnet run --project src/EasyPDF.App -- ~/file.pdf

# Self-contained app: artifacts/publish/linux-x64/<App> (shown name)
dotnet publish src/EasyPDF.App -p:PublishProfile=linux-x64

# .deb (Debian/Ubuntu) and .tar.gz (any distribution): one step incl. publish, on Linux with PowerShell 7 (needs: sudo apt install clang zlib1g-dev)
pwsh installer/linux/build-linux-on-linux.ps1   # or by hand after the publish above: python3 installer/linux/build_linux.py
sudo apt install ./artifacts/installer/<App>-<version>-Linux-x64.deb
```
The publish has to run on Linux (Native AOT); `build_linux.py` itself needs neither dpkg nor Linux. The `.deb` (package `<app>`: the shown name in lower case) installs to `/opt/<App>`, adds the command `<app>`, the application menu entry and the context menus for Dolphin and Nemo.

## macOS (Apple Silicon, macOS 14 or later)
Intel Macs and macOS 13 are not supported: ONNX Runtime, which the text recognition (OCR) uses, has no Intel build and needs macOS 14.
```bash
dotnet build EasyPDF.slnx
dotnet test EasyPDF.slnx
dotnet run --project src/EasyPDF.App -- ~/file.pdf

# Self-contained app: artifacts/publish/osx-arm64/
dotnet publish src/EasyPDF.App -p:PublishProfile=osx-arm64

# <App>.app (signed ad hoc) in a .dmg: artifacts/installer/<App>-<version>-macOS-AppleSilicon.dmg (shown name)
bash installer/build-macos-on-macos.sh   # other brand: bash installer/build-macos-on-macos.sh <id>
```
The disk images can only be built on a Mac. Without an Apple Developer ID the app is only ad hoc signed, not notarized, so macOS blocks a downloaded copy. After dragging it to *Applications*, remove the quarantine flag once:
```
xattr -cr /Applications/<App>.app
```
Without Terminal: start it once, close the warning, then System Settings ▸ Privacy & Security ▸ *Open Anyway*. A copy built on the same Mac starts without warning.

## Without local tools: GitHub Actions
The workflow *Installers* (`.github/workflows/installers.yml`) builds the `.msi`, the portable Windows `.zip`, `.deb`/`.tar.gz` and the `.dmg` on GitHub, tests the MSI and `.deb` upgrades, starts the installed app on Linux and the unpacked portable app on Windows and runs the OCR tests on Linux and macOS; all tests run on Windows (in `build-windows-on-windows.ps1`, the other jobs skip them with `EASYPDF_NO_TESTS`), so a failing test stops the Windows installers there. It runs manually (Actions ▸ Installers ▸ Run workflow, or `gh workflow run installers.yml --ref <branch>`); the automatic trigger is commented out in the workflow. *platform* builds all three (default) or only one, *brand* picks the brand profile (empty: the default in `Directory.Build.props`), and the `.dmg` is mounted and the app started once. Each runner brings the toolchain Native AOT links with (the Linux job installs `clang` and `zlib1g-dev`).

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

## Web version (container)
The web version is the same user interface in the browser: `src/EasyPDF.Browser` (Avalonia as .NET WebAssembly, drawn into
a canvas) talks to `src/EasyPDF.Server` (ASP.NET Core with pdfium), which also serves the browser program. Design and
security: [DESIGN.md "Web version"](DESIGN.md#web-version).

**Prerequisites:** .NET 11 SDK with the WebAssembly workload (`dotnet workload install wasm-tools`; its Emscripten needs
Python 3); for the image PowerShell 7 (`pwsh`) and Podman (or Docker).

**Image:** `./BuildContainer.sh` (Linux) or `BuildContainer.cmd` (Windows) runs `installer/container/build-container.ps1`:
`git pull`, vulnerability check of the NuGet packages, all tests, `podman build` (the `Dockerfile` publishes server and
browser program inside the SDK container and runs the core and server tests once more), then pushes
`<name>-web:<version>` and `:latest` (name = shown name of the brand in lower case) to the registry (default the private one
of the NAS, `-Registry host:port` for another). Options: `-NoPush` (trial build, local only), `-NoTests`, `-NoPull`,
`-AllowDirty` (uncommitted changes stop it otherwise), `-Brand <id>`. The runtime image is `runtime-deps` *chiseled-extra*
(no shell, non-root), the server is published self-contained with ReadyToRun (Native AOT is not needed on the server).
The browser program is published with WebAssembly AOT, which takes about 15 minutes of the image build (and a few GB of
memory); a plain `dotnet build` is not affected.

**Measuring the web version** (`tools/web-timing/measure.mjs`, Node 22+, or Node 20 with `--experimental-websocket`;
Firefox): starts Firefox headless with a fresh profile, opens `<url>?benchmark` twice (empty cache, second visit) and prints
the program's timings (start phases, dialogs opened by the program itself) and the bytes that came over the network:
```
node tools/web-timing/measure.mjs http://localhost:5199/
```
In a normal browser `?timing` writes the same lines (`[timing] …`) to the developer console.

**Run it locally** (development, without container):
```
dotnet publish src/EasyPDF.Browser -c Release -o artifacts/web-dev/browser
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5199 \
  Web__WebRoot=$PWD/artifacts/web-dev/browser/wwwroot dotnet run --project src/EasyPDF.Server
```
Then open `http://localhost:5199` (browsers send the `Secure` cookies to `localhost` over HTTP, elsewhere only over HTTPS).

**Configuration** (section `Web` of `appsettings.json`; in the container `/app/appsettings.Production.json`, mounted from the
host, or environment variables `Web__…`): `Authentication:Mode` `None` (no sign-in, anonymous sessions) or `Oidc` (OpenID
Connect: `Authority`, `ClientId`, `ClientSecret`, optional `RequiredGroup`/`RequiredRole`; callback path `/signin-oidc`),
`Storage` (folders `/data/files`, `/data/keys`, `SharedFolder` `/data/shared` with stamps and templates for all users, read-only; `DownloadsFolder` `/data/downloads` with the desktop packages offered for download, read-only; `IdleTimeoutMinutes` 30, `MaxAgeHours` 8, upload and quota limits),
`RateLimits`, `ReverseProxy` (`TrustContainerGateway`, `TrustedProxies`) and `AllowedHosts`. The container writes only to
`/data` and `/tmp`, so it runs with `--read-only`; it checks its own health with `/app/EasyPDF.Server --healthcheck`.
The start script for the NAS (volumes, hardening options, reverse proxy) lives in the ContainerZoo repository
(folder `EasyPDFWeb`).

**Desktop downloads:** the web version offers the desktop packages (button with the download arrow in the header,
shown only when there are some). They are not part of the image: the server lists the files in `DownloadsFolder` that are
named like the installer scripts name them – `<App>-<version>-Windows-Installer.msi`, `-Windows-portable.zip`,
`-macOS-AppleSilicon.dmg`, `-Linux-x64.deb`, `-Linux-x64.tar.gz` (`DesktopPackage` in Core) – the newest of each kind,
and the dialog marks those for the browser's system. New installers only need new files in the folder (no new image); on
the NAS `EasyPDFWeb/update-downloads.sh` in the ContainerZoo repository fetches them from the release *latest* and adds
the Linux packages built locally.

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
- Brands for the development repository only (their `README.md` starts a paragraph with "Development repository only": `grep -l "Development repository only" branding/*/README.md`) never go into a public
  release repository: when a carried commit adds or changes such a folder, remove it there before the push
  (`git rm -r branding/<id>` and `git commit --amend --no-edit` on the cherry-picked commit, which is not pushed yet).
  Private release repositories may hold them.
- Check before the push: `git diff --stat FETCH_HEAD HEAD` lists only the files above (and, in a public release
  repository, the folders of development-only brands as missing).

The build number is the number of commits, so the same change has a lower one in a release repository.

## Demo videos
`branding/<id>/make-presentation-videos.cmd` records the introduction in German and English for that brand
(`tools/DemoVideo`, drives the real app headless; ffmpeg is downloaded into a temp folder for the run) into
`branding/<id>/videos/` (`introduction.de.mp4`, `introduction.en.mp4`); they are committed and shipped with the app
(video button in the header). With `--web` the tool records the web version into `branding/<id>/videos/web/`; the
browser program puts those into the page (`wwwroot/videos/`), so they are part of the image (see branding/README.md).
