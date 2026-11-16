# EasyPDF

*Deutsch: [README_DE.md](README_DE.md)*

A desktop app for Windows, macOS and Linux to **read and reorganize PDF documents** – fast, simple and completely
offline. Every change happens on screen first and can be undone; the file only changes when you save.

On request the app is **white-label**: any company can switch it to its own name and branding (icon, colours and
publisher), for example for internal use (see [Customization](#customization)). The original is EasyPDF; below, `<App>`
stands for the name of the brand you use. The copyright and license notice must be kept (MIT, see [License](#license)).

**Download:** installers for Windows, Linux and macOS and a portable Windows version (.zip, unpack and run) in the pre-release
[latest](../../releases/tag/latest) (built by GitHub Actions,
[docs/BUILDING.md](docs/BUILDING.md#without-local-tools-github-actions)).
**Introduction video:** a two-minute introduction (English and German) comes with the app: the video button in the
header plays it. It is recorded per brand with `branding/<id>/make-presentation-videos.cmd`.

## Features

### Read
- Pages side by side with horizontal scrolling – mouse wheel, arrow keys, or **swipe with the mouse** (a fast swipe glides on)
- Fit to height, width or page, free zoom around the pointer (Ctrl/Cmd + mouse wheel, pinch)
- Page sidebar with thumbnails that follow the page you read; jump to any page with a click or Ctrl/Cmd+G; drag its edge to make it (and the thumbnails) wider
- **Find text** with Ctrl/Cmd+F: all matches are highlighted, Enter/F3 jumps to the next one (searches the text layer and recognized text)
- Select and copy text; fast even with very large files (only visible pages are loaded)

### Organize pages
- Thumbnail grid with multi-select and **drag & drop** to reorder
- Rotate (quickly, or for all/selected pages/a range like `1-3, 5`), delete, insert blank pages
- Insert pages of other PDFs before or after a page, replace pages with pages of another document
- **Merge** several files into a new document, **extract** pages, **split** a document into parts
- Pages from other files carry a coloured marker, so you always see where they came from

### Add to pages
- **Stamps**: signatures, company stamps, "Paid", number stamps 00–20 … from a stamp folder; stamps with fields fill in
  the date when placed ("Received 9 Nov 2026")
- **Text**: click on the page and type; colour, font and size are remembered; saved as real, searchable PDF text
- **Templates**: text snippets with placeholders for date, time, user, file name, page number
- **Copy area**: frame part of a page and paste it as a picture on another page or into another document

### Protect and share
- **Black out / white out**: covers confidential passages; on saving, everything under the boxes is removed from the
  file for good (text, pictures, drawings, form entries)
- **Text recognition (OCR)** for scans, German and English: adds an invisible text layer, so scanned pages become
  searchable and copyable in every PDF viewer – offline, the models ship with the app
- **Reduce file size** (scans and pictures with more resolution than needed), **print**, **send by email** – always
  with your current changes, even unsaved ones
- Undo/redo for every step; saving keeps bookmarks, metadata and forms

### Operation
- One window per document; menu bar (native on macOS), ribbon, keyboard shortcuts, recent files, drag & drop of files
- Built-in quick guide (F1) and introduction video, light and dark theme, English and German
- File manager integration: *Read with <App>* and *Merge with <App>* in the right-click menu (Windows Explorer,
  Dolphin, Nemo; *Open With* in Finder and GNOME Files)

## Customization

### Your own brand (white-label)
Everything that identifies the app comes from one brand profile in `branding/<id>/` – no code changes:

| What | Where it shows |
|---|---|
| **Name** | window title, all texts, program file, installer, start menu, context menu, install and settings folders |
| **Two brand colours** (dark primary, light accent) | the whole colour scheme in light and dark theme is derived from them |
| **Icon or logo** | program icon (all sizes), brand header of the window; drawn in the brand colours or made from your logo |
| **Company logo** | Help ▸ About |
| **Publisher, copyright, author** | Help ▸ About, file properties, installers |
| **Demo videos** | recorded with the brand's name, colours and icon |

Each brand installs side by side with others, with its own settings. One brand is built at a time, without a choice
`easypdf` (`<Brand>` in `Directory.Build.props`); another one with `-p:Brand=<id>` (or the variable `EASYPDF_BRAND`, or
`-Brand <id>` of the installer scripts). How to create and change a brand:
[branding/README.md](branding/README.md); with Claude Code the skill `branding` does it step by step.

### Stamps and templates
- **Stamp folder** (default `Documents/<App>/Stamps`, any folder in Settings): every PNG in it is a stamp, subfolders
  become sections of the gallery. Copy pictures in with the file manager – or design stamps in the built-in **stamp
  editor**: a picture plus text fields with placeholders (e.g. the date of a "Received" stamp), stored inside the PNG,
  so a stamp is one file that can be passed on.
- **Templates folder** (default `Documents/<App>/Templates`): one text file per template, editable in the app or any
  editor. Placeholders such as `{$DATE:yyyy-MM-dd}`, `{$USER}`, `{$FILENAME}`, `{$PAGE}`/`{$PAGES}` are filled in when
  placed.
- Sample stamps and templates are provided at the first start; deleted samples don't come back.

### Settings
Theme (system, light, dark), language (system, English, German), stamp and templates folder, warning before
overwriting a file with blacked-out content, and "Open PDF files with <App>" (make it the default PDF program).

## Command line

```
<App> [--read | --merge] [file.pdf ...]          (Linux: <app>, the name in lower case)
```
One PDF opens for reading; several open the *Merge PDFs* dialog in the given order. `--read` opens each file in a
window of its own, `--merge` always goes to the merge dialog (files of several processes started at the same time are
collected into one dialog).

## Building
.NET 11 SDK, then `dotnet build EasyPDF.slnx` and `dotnet test EasyPDF.slnx`. Prerequisites, installers for Windows
(MSI, portable .zip), Linux (.deb/.tar.gz) and macOS (.dmg), versioning and GitHub Actions: **[docs/BUILDING.md](docs/BUILDING.md)**.

## Documentation
- [docs/BUILDING.md](docs/BUILDING.md) – building, installers, CI
- [branding/README.md](branding/README.md) – brand profiles
- [docs/DESIGN.md](docs/DESIGN.md) – design decisions and UX description
- [CLAUDE.md](CLAUDE.md) – architecture and conventions
- [docs/TEST_CHECKLIST.md](docs/TEST_CHECKLIST.md) – manual test list (especially macOS/Linux)

## License
MIT – see [LICENSE](LICENSE). The bundled open source components and their licenses are listed in
[licenses/THIRD-PARTY-NOTICES.txt](licenses/THIRD-PARTY-NOTICES.txt) (also in the app: Help ▸ About ▸ Third-party licenses).
