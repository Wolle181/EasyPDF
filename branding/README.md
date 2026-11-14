# Brands

The app can be built for different companies, each with its own name, icon, colours and publisher. Each brand is a
folder here; everything that belongs to one brand lives in its folder and nowhere else in the repository.

| Folder | Brand |
|---|---|
| `easypdf/` | the original app, **EasyPDF** (default); template for new brands |
| others | one folder per company or variant (each has a `README.md`) |

## Selecting the brand

One brand per build. Without a choice it is `easypdf` (`<Brand>` in `Directory.Build.props`).

| Where | How |
|---|---|
| any `dotnet` command | `-p:Brand=<id>` |
| for a whole machine or shell | environment variable `EASYPDF_BRAND=<id>` (Windows: `setx EASYPDF_BRAND <id>`, then open a new window) |
| installer scripts | `installer\build-windows-on-windows.cmd -Brand <id>` (also `-p:<id>`, `-p:Brand=<id>` or just `<id>`; unknown parameters stop the script), `pwsh installer/linux/build-linux-on-windows.ps1 -Brand <id>`, `bash installer/build-macos-on-macos.sh <id>` |
| CI | workflow *Installers*, input `brand` (`gh workflow run installers.yml -f brand=<id>`) |

After switching, build everything again (the installer scripts always do).

## What a brand folder holds

| File | Content |
|---|---|
| `Branding.props` | shown name (`AppDisplayName`), brand colours, publisher (`Company`, `Copyright`), About line (`AppAuthor`), installer ids (`MsiUpgradeCode`, `MacBundleId`), `BrandWords`; every value is explained in `easypdf/Branding.props` |
| `icons/` | `app.ico`, `app.icns`, `app-<size>.png`, written by `tools/icon/make_icon.py`; with a logo also `logo.png` (+ `logo-small.png`) and `header-logo.png`; `company-logo.svg`: the company logo in Help ▸ About (optional, drawn as vector graphics) |
| `make-presentation-videos.cmd` | records the introduction videos of this brand (German and English) into `videos/` (Windows; downloads ffmpeg for the run), see [Presentation videos](#presentation-videos) |
| `videos/` | `introduction.de.mp4`, `introduction.en.mp4`: shipped with the app (`videos/` next to the program), the video button in the header plays the one of the UI language with the system's video player; without videos the button is hidden |
| `README.md` | what the brand is for, its look and where its design comes from |
| anything else | the brand's own material, e.g. `easypdf/logos/` |

The shown name reaches everything users see: UI texts (`{App}` in `Strings*.resx`), window title, About, start menu,
installed apps, context menus, Linux menu entry, macOS app, program file (`<name>.exe`), install folder, registry keys,
installer file names, the user's folders (settings, stamps, templates, temp), the Linux package and command (lower case)
and the names of the single-instance mutex and pipe. Each brand has its own installer ids and folders, so two brands can be
installed and run side by side. The technical name EasyPDF stays in projects and namespaces.

## Presentation videos

`branding/<id>/make-presentation-videos.cmd` records the brand's introduction videos in German and English
(`branding/<id>/videos/introduction.de.mp4` and `introduction.en.mp4`, a few minutes and about 8 MB each). They are
committed and shipped with the app: the build copies them to `videos/` next to the program, and the video button in the
header plays the one of the UI language (else the English one). Record them again after visible changes of the user
interface, then commit them. It builds from the working copy as it is, so run `git pull` first. ffmpeg is downloaded into a temp folder for the run and deleted afterwards.

The dialogs for merging and splitting show the file paths, so the sample files go into a tidy folder,
`Documents\Beispiele` (German) or `Documents\Samples` (English). The folder is created for the recording and deleted
afterwards. If it already exists or cannot be created, the tool uses a folder under `%TEMP%\easypdf-demovideo\` and the
video shows that long path instead.

**Controlled folder access** (Windows Security, ransomware protection) blocks unknown programs in Documents. They get
"file not found" back instead of "access denied". The tool then prints `Cannot create … allow <path>\DemoVideo.exe …`.
For tidy paths, allow exactly that program (Windows Security → Virus & threat protection → Ransomware protection →
Allow an app through Controlled folder access; it is `tools\DemoVideo\bin\Release\net11.0\DemoVideo.exe`) and record
again.

## Rule: no brand in the shared files

Docs, code, comments, tests and scripts outside `branding/` do not name a brand: write "the app", `{App}` (UI texts),
`<App>` (docs) or read the value from the build. The test `BrandingTests.BrandNamesAndWordsStayInTheirProfile` fails when
a brand's shown name (other than EasyPDF) or one of its `BrandWords` appears outside its folder. Brands of the same
`Company` (e.g. colour variants of one brand) may name each other in their folders.

## New brand

1. Copy `easypdf/` to `branding/<id>/` (lower case, no spaces), delete what belongs to EasyPDF only (`logos/`, `icons/logo.png`, `icons/header-logo.png`, `icons/company-logo.svg`), write its `README.md`.
2. In `Branding.props`: name, colours (set `BrandHeaderColor` to a colour of the new brand or delete it), publisher, `AppAuthor`, `BrandWords`; a **new** `MsiUpgradeCode` (`[guid]::NewGuid()`
   in PowerShell) and a new `MacBundleId`. Never change these two once the brand is shipped.
3. Icon: `python tools/icon/make_icon.py --brand <id>` (needs Pillow: `pip install pillow`) draws the mark in the brand
   colours; or `--logo logo.png [--logo-small logo-16.png] [--margin 4]` makes it from a logo (PNG with transparency, at
   least 1024 px). With a logo the header shows it too (`header-logo.png`); the header is dark, so the logo needs light parts.
   Look at `artifacts/icon-preview-<id>.png`.
4. Company logo for Help ▸ About (optional): `python tools/icon/make_icon.py --brand <id> --company-logo <file.svg>` writes
   `icons/company-logo.svg` (the app draws it as vector graphics, sharp at every display scaling; a transparent logo gets
   a white card so it shows in the dark theme too). A logo that only exists as a picture is traced into an SVG first:
   `python tools/icon/trace_logo.py <picture> logos/<name>.svg` (needs `pip install pillow vtracer`; give the largest file
   there is, `--corner 150` for round letters). Keep the original and the SVG in the brand folder (`logos/`).
5. Build and test with the brand: `dotnet test EasyPDF.slnx -p:Brand=<id>`; look at both themes:
   `EASYPDF_SCREENSHOTS=<folder> dotnet test tests/EasyPDF.App.Tests -p:Brand=<id> --filter ScreenshotTests`.
6. Installers and videos as above.

## Changing a brand

- **Colours:** set `BrandPrimaryColor` (dark: band, selection, white text on it; its hue tints the greys) and
  `BrandAccentColor` (light signal colour: line under the band, Save button; primary-coloured text on it). Every colour of
  both themes is a shade of them (`src/EasyPDF.App/Styles/BrandColors.cs`, `{st:BrandColor …}` in `App.axaml`). Then draw
  the icon again (`make_icon.py --brand <id>`); `MainWindowAndDialogsShowTheAppIcon` notices an icon in old colours.
  Optional `BrandHeaderColor`: top of the header gradient (it ends in the primary colour); without it the header starts in
  a slightly lighter primary colour.
  To try colours without editing: `-p:BrandPrimaryColor=#0B4D2C "-p:BrandAccentColor=#F5A623"` on the screenshot command.
- **Name:** set `AppDisplayName` (plain ASCII without spaces is safest). Users of that brand then get a new install
  folder, start menu entry and empty settings (folders are named after the app); on Linux the old package must be removed
  by hand. Record the videos again.
- **Drawing of the mark:** in `make_icon.py` (lines on a 24 × 24 grid) and, for the header, `BrandMark` in
  `src/EasyPDF.App/Views/MainWindow.axaml` (geometry `Icon.Concatenate` in `Styles/Icons.axaml`); it is the same for all brands.

With Claude Code the skill `branding` (`.claude/skills/branding`) does these steps including the checks.
