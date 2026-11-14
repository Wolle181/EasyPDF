---
name: branding
description: Create, switch or change a brand of the app - shown name, program icon or logo, brand colours (light and dark theme), publisher, copyright, About author line, installer ids, presentation videos. Each brand is a profile in branding/<id>/ (default easypdf); one is built at a time. Use when the user wants a new customer brand, another name ("App umbenennen", "heißt jetzt X"), other colours ("andere Farben", "Farbschema", "CI-Farben"), a new icon/logo, or to build/test a particular brand.
---

# Brands

Read `branding/README.md` first: it describes the profiles, how the build selects one (`-p:Brand=<id>`,
`EASYPDF_BRAND`, `-Brand` of the installer scripts; default `easypdf` in `Directory.Build.props`) and the rule that no
shared file names a brand.

## How the values reach the app

`Directory.Build.props` imports `branding/$(Brand)/Branding.props`; `$(BrandDir)` is that folder. Every value is set
there and nowhere else:

| Value | Read by |
|---|---|
| `AppDisplayName` | UI texts (`{App}` in `Strings*.resx` via `Loc.T`; `Loc.AppName` = Core's `AppInfo.Name`, assembly metadata from `EasyPDF.Core.csproj`), user folders (settings, stamps, templates, temp), mutex/pipe names (`InstanceChannel`, `MergeInstances`), `<AssemblyName>` (program file, process, WM_CLASS), `$(var.AppDisplayName)` in `Package.wxs` (installed apps, start menu, context menu, registry, install folder), `@APPNAME@`/`@PACKAGE@` in `installer/linux` and `installer/macos/Info.plist`, installer file names |
| `BrandPrimaryColor`, `BrandAccentColor`, optional `BrandHeaderColor` (top of the header gradient) | `Styles/BrandColors.cs` (assembly metadata from `EasyPDF.App.csproj`), every colour in `App.axaml`/`Styles/Ribbon.axaml` as `{st:BrandColor Primary\|Accent\|Header, S=…, L=…}` or `Lighten=…`; `tools/icon/make_icon.py` |
| `Company`, `Copyright`, `AppAuthor` | .exe details, MSI Manufacturer, .deb Maintainer, macOS Finder info; About line (`Services/VersionInfo.cs`) |
| `MsiUpgradeCode`, `MacBundleId` | `Package.wxs` (`$(var.UpgradeCode)`), CI upgrade test; `Info.plist` (`@BUNDLEID@`) |
| `BrandWords` | only the test `BrandingTests.BrandNamesAndWordsStayInTheirProfile` |
| `icons/` | `EasyPDF.App.csproj` (`ApplicationIcon`, `Assets/AppIcon.ico`, `Assets/AppIcon-128.png`, `Assets/BrandLogo.png` from `header-logo.png`, `Assets/CompanyLogo.svg` from `company-logo.svg` for Help > About, drawn as vector graphics by `Styles/SimpleSvg.cs`), WiX (`$(var.AppIcon)`), `build_linux.py` (`app-<size>.png`), `build-macos-on-macos.sh` (`app.icns`), DemoVideo |

Colour rules: primary dark (white text readable on it, lightness ≲ 25 %), its hue tints all greys; accent light and
bright (primary-coloured text on it). Fixed on purpose: white, the red of delete icons, source colours in Organize,
text selection, search hits, the text/stamp colour choice; `StylesUseNoFixedColors` rejects any other `#RRGGBB` in `*.axaml`.

## New brand

1. Ask for: id (folder name, lower case), shown name (plain ASCII, no spaces: the MSI uses codepage 1252), two colours,
   publisher/copyright, About author line, logo or drawn mark, words that identify the customer (`BrandWords`).
2. Copy `branding/easypdf/` to `branding/<id>/` without `logos/`, `icons/logo.png`, `icons/header-logo.png` and `icons/company-logo.svg`; set the values in `Branding.props` (`BrandHeaderColor`: the new brand's or deleted); new
   `MsiUpgradeCode` (`[guid]::NewGuid()`), new `MacBundleId`; write its `README.md` (who it is for, look, origin of the design).
3. Icon (step below), then build/test/look (step below) with `-p:Brand=<id>`.

## Change a brand

- **Name:** `AppDisplayName` only. Users of that brand get a new install folder, start menu entry and empty settings;
  Linux needs the old package removed by hand. Mention it.
- **Colours:** both values; check the rules above and warn about weak contrast. A single shade that looks wrong is
  fixed with its `S`/`L`/`Lighten` in `App.axaml`, never with a fixed colour. Then draw the icon again.
- **Publisher, author, ids:** never change `MsiUpgradeCode`/`MacBundleId` of a shipped brand.

## Icon

`python tools/icon/make_icon.py --brand <id>` draws the mark in the brand colours into `branding/<id>/icons/`;
`--logo <file> [--logo-small <file>] [--margin 4]` makes it from a logo instead (kept as `icons/logo.png`, later runs reuse
it; also writes `icons/header-logo.png` for the dark header: check it stands out). Needs Pillow; without it make a venv
in the scratchpad (`python -m venv <scratchpad>/venv`, `<venv>/Scripts/python -m pip install pillow`) and run the script
with that Python; without any Python get the portable `python` package from nuget.org into the scratchpad. Look at
`artifacts/icon-preview-<id>.png`. Company logo for Help > About: `--company-logo <file.svg>` writes only
`icons/company-logo.svg` (transparent logos get a white card); a logo that is only a picture is traced first with
`tools/icon/trace_logo.py <png> <svg>` (Pillow + vtracer; `--corner 150` for round letters, the default keeps right angles
sharp; check the result enlarged). Keep the original and the SVG in the brand folder (`logos/`). A new drawing changes `make_icon.py` and `BrandMark` in `Views/MainWindow.axaml`
together, for all brands.

## Build, test, look

1. `dotnet build EasyPDF.slnx -p:Brand=<id>` and `dotnet test EasyPDF.slnx -p:Brand=<id>` green; also the default brand
   (without `-p:Brand`). `MainWindowAndDialogsShowTheAppIcon` fails when the icon was not drawn again after a colour change.
2. Screenshots of both themes: `EASYPDF_SCREENSHOTS=<scratchpad folder> dotnet test tests/EasyPDF.App.Tests -p:Brand=<id> --filter ScreenshotTests`,
   view `light-*.png` and `dark-*.png`. Colours can be tried without editing: add `"-p:BrandPrimaryColor=#0B4D2C" "-p:BrandAccentColor=#F5A623"`
   (then `BrandColorsComeFromTheBrandProfile` fails, as expected).
3. Optional MSI check without the full installer (needs `artifacts/publish/win-x64` of that brand):
   `dotnet build installer/windows/EasyPDF.Installer.wixproj -c Release -p:Version=1.0.0.999 -p:Brand=<id>`, read
   `ProductName`, `UpgradeCode`, `CTXREAD`, `CTXMERGE` (Property table), Shortcut, Directory and Registry tables with the
   `WindowsInstaller.Installer` COM object (quote column names with backticks, `Key` is reserved), then delete the test MSI.
4. Videos: `branding/<id>/make-presentation-videos.cmd` (output `branding/<id>/videos/introduction.<de|en>.mp4`, committed
   and shipped with the app; the video button in the header plays them).

## Afterwards

Never write a brand's name or words outside its folder (docs: `<App>`/"the app", UI: `{App}`). Docs: brand-specific
notes go into `branding/<id>/README.md`; the shared look stays in `docs/DESIGN.md` ("Look"). Commit (English message), push.
