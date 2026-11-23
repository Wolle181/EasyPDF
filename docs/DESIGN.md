# Design: how this application is built and meant to work

> Design, decisions and UX of the app. Keep this file up to date when the concept changes.
> The app is built per brand (name, icon, colours, publisher: [branding/README.md](../branding/README.md)); `<App>` below stands for the shown name.

## Context
Goals of the app:
1. UI on Avalonia so it runs on Windows, macOS and Linux.
2. Menu bar + ribbon, on all OSes.
3. Reorder pages in a thumbnail view via drag & drop.
4. Right-click on a page (Reading and Organize mode) → context menu with "Insert before this page" / "Insert after this page" → file dialog.
5. Extras: extract/split pages, free zoom, outline/bookmarks sidebar, recent files.
6. Help menu with Info (About) and version information.
7. Rotate dialog (ribbon entry): direction (90° clockwise, 90° counter-clockwise, 180°) + pages (all, range, current page).
8. Concatenate: combine several PDF files into one (shown as "Merge" / „Zusammenführen“).
9. Settings dialog: light/dark mode and language selection (English + German from the start).

## Decisions
- **Ribbon:** own lightweight ribbon (no third-party package).
- **Thumbnails:** separate **Organize mode** (full thumbnail grid instead of the reading view).
- **macOS:** native global menu bar.
- **Documents:** one document per window, several windows in one process (see "Several documents"); a document can contain pages from several files.
- **Save:** Save overwrites the original (temp file + replace), plus Save As.
- **Languages:** English and German from the start (`Strings.resx` + `Strings.de.resx`); selectable in Settings.
- **Target platforms:** win-x64, osx-arm64, linux-x64 (no osx-x64: macOS needs Apple Silicon and macOS 14 because of ONNX Runtime for OCR, see [Text recognition (OCR)](#text-recognition-ocr).)
- **Naming:** the shown name is set separately (`AppDisplayName`, see "Look"), per brand in `branding/<id>/`; the technical name is `EasyPDF` (projects, namespaces `EasyPDF.Core` / `EasyPDF.App`, internal ids).
- **Insert:** always all pages of the chosen file(s).
- **Theme:** Avalonia Fluent; default follows the OS, can be fixed to Light or Dark in Settings.
- **Version:** the first three parts are maintained manually as `<VersionPrefix>` in `Directory.Build.props`, the build number is the commit count, the commit hash is appended automatically.

## Technology stack
| Area | Choice | Why |
|---|---|---|
| UI | Avalonia 12.1.x, `net11.0` (.NET 11; SDK in `global.json`, framework in `Directory.Build.props`) | cross-platform, draws its own controls (ribbon looks the same everywhere) |
| MVVM | CommunityToolkit.Mvvm 8.4 | `RelayCommand`/`ObservableProperty`; menu, ribbon and context menu bind to the *same* commands |
| PDF | **PDFiumCore 155.x** (P/Invoke, natives via `bblanchon.PDFium.*` for win/linux/osx incl. arm64) | rendering **and** editing; verified present in the bindings: `FPDF_LoadCustomDocument` (streaming), `FPDF_RenderPageBitmap`, `FPDF_MovePages`, `FPDFPageDelete`, `FPDFPageSetRotation`, `FPDF_ImportPagesByIndex`, `FPDF_CreateNewDocument`, `FPDF_SaveAsCopy`, `FPDFBookmark*`, `FPDFDestGetDestPageIndex` |
| Tests | xUnit | Core logic without UI |

## Solution layout
```
EasyPDF.slnx
Directory.Build.props       <VersionPrefix> (bumped manually), brand selection (imports branding/<Brand>/Branding.props), shared for all projects
src/EasyPDF.Core/          net11.0, no UI dependency
  Pdfium/PdfiumWorker.cs    one dedicated thread for ALL pdfium calls (pdfium is not thread-safe); Task-based, cancellable priority queue
  Pdfium/PdfSource.cs       open via FPDF_LoadCustomDocument + FileStream (keep GetBlock delegate alive), page count, page sizes, render to BGRA buffer
  Pdfium/OutlineReader.cs   bookmark tree (title, source page index, children)
  Documents/EditableDocument.cs   page list model + undo/redo
  Documents/PdfSaver.cs     Save / Save As / Extract / Split (see below)
  Documents/PageRangeParser.cs    parses "1-3, 5, 8-10" (1-based, validated against page count, clear error messages); reused for later page-range features
  Layout/HorizontalLayout.cs      pure layout math (FitPage, FindPageAt, VisibleRange, anchor on relayout) + zoom
  Caching/PageImageCache.cs       LRU with byte budget (never evict visible pages)
  Settings/AppSettings.cs   JSON in the per-user app-data folder: theme, language, recent files, window size/position, ribbon collapsed, last zoom
src/EasyPDF.App/           desktop program (start-up only)
src/EasyPDF.UI/            Avalonia user interface shared by desktop and web
  App.axaml
  ViewModels/MainViewModel.cs, ReadingViewModel.cs, OrganizeViewModel.cs, OutlineViewModel.cs, AboutViewModel.cs
  Views/MainWindow.axaml    DockPanel: NativeMenu / Ribbon / content (Reading|Organize) / status bar
  Views/AboutWindow.axaml
  Controls/Ribbon/          Ribbon (TabControl + ControlTheme), RibbonGroup, RibbonButton (large 32px / small 16px), RibbonComboBox
  Controls/HorizontalPageView.cs  reading view
  Controls/ThumbnailGrid    Organize mode
  Controls/PageContextMenu  shared right-click menu
  Dialogs/                  MessageDialog, UnsavedChangesDialog, SplitDialog, RotateDialog, ConcatenateDialog
  Resources/Strings.resx, Strings.de.resx   all UI texts (English neutral, German)
  Localization/Loc.cs       observable string provider: XAML binds via `{loc:Tr Key}`; changing the culture raises PropertyChanged → all texts (incl. NativeMenu) update live
  Views/SettingsWindow.axaml
  Assets/                   fonts (Barlow) and a few bitmap icons; all other icons are vector geometries in Styles/Icons.axaml
tests/EasyPDF.Core.Tests/
```

## Core design
**Document model (non-destructive editing):** `EditableDocument` holds `ObservableCollection<PageEntry>` with `PageEntry { Id, PdfSource Source, int SourceIndex, int ExtraRotation }`.
- `Move(selection, insertIndex)`, `Rotate(pages, +90 | −90 | 180)`, `Delete(selection)`, `InsertFrom(sources, index)` – each operation is one undoable command (undo/redo stacks), sets `IsDirty`.
- Rendering always uses the source page + `ExtraRotation` (rotate parameter of `FPDF_RenderPageBitmap`) → edits are instant, files are untouched until Save.
- Inserted files stay open as additional `PdfSource`s until Save/Close.

**Save (keeps bookmarks, metadata, forms):** PDFium does not copy bookmarks when importing into a *new* document. So Save/Save As instead **edits a fresh copy of the main file**:
open the main file again → `FPDF_ImportPagesByIndex` for pages from other files (appended at the end) → `FPDFPageDelete` for removed pages → `FPDF_MovePages` into the target order → `FPDFPageSetRotation(original + extra)` → `FPDF_SaveAsCopy` streams into a temp file in the target folder → when overwriting the open file: close its sources, `File.Replace`, reopen. Bookmarks point to page objects and therefore follow the reordering; bookmarks to deleted pages lose their target.
**Extract / Split** create *new* documents (`FPDF_CreateNewDocument` + `FPDF_ImportPagesByIndex` + rotation), without bookmarks.

**Loading large files:** streaming (`FPDF_LoadCustomDocument`, the file is read block by block). Page sizes are read on the worker in batches after opening; the layout starts with the first page size as an estimate and updates as sizes arrive → no blocking.

**Rendering:** priority queue on the worker (visible > prefetch > thumbnails); requests for pages that scrolled out of view get cancelled; results come back as `WriteableBitmap` (Bgra8888, white background) via `Dispatcher.UIThread`.

## UI design
**Look (house style):** everything brand-specific comes from the brand profile (`branding/<id>/Branding.props`, [branding/README.md](../branding/README.md)): the shown name `AppDisplayName` (UI texts via `{App}` in the resx, .exe description, installed apps, start menu, file manager context menu, Linux desktop entry, macOS app name, program file `<App>.exe`, install and user folders, registry keys, installer files, the Linux package/command in lower case, single-instance mutex and pipe), publisher and author line, installer ids, the two brand colours and the program icon (window, taskbar, .exe, installers, About; drawn by `tools/icon/make_icon.py` into `branding/<id>/icons/`, or made from a logo). Only projects, namespaces and internal ids keep the technical name EasyPDF. The style itself is the same for every brand: sober corporate design with a dark primary colour, the accent colour as the only signal colour, white and cool greys, typeface **Barlow** (embedded, `Assets/Fonts`, SIL OFL). Every colour of both themes is a shade of the two brand colours `BrandPrimaryColor`/`BrandAccentColor` (`Styles/BrandColors.cs`: hue of the brand colour, share of its saturation, own lightness; `{st:BrandColor …}` in `App.axaml`), the greys too (primary hue, little saturation), and `make_icon.py` reads them for the icon. Below, "primary" and "accent" are these two colours.
- Brand header on top: a slight gradient in the primary colour (its top can be set as `BrandHeaderColor`) with the mark (white pages, accent plus, in a dark square framed in the accent colour; or the brand's logo), the product name and a subtitle (`BrandSubtitle`, "Read, organize and combine PDFs – locally and offline"), the gear/▶/?/ⓘ buttons (settings, introduction video, quick guide, About; white icons) on the right. ▶ plays the brand's video in the UI language (`videos/introduction.<language>.mp4` next to the program, else English) with the system's video player and is hidden when the brand has none (a player inside the app would need about 100 MB of libVLC per platform). The web version plays its own videos (`branding/<id>/videos/web/`, recorded with `tools/DemoVideo --web`, in the image as `wwwroot/videos/`) in a video element of the page. It stays visible when the ribbon is collapsed.
- Band in the primary colour with the ribbon tabs (muted white, selected tab white on a lighter shade); under it the accent line and the white ribbon panel; group captions and separators in quiet greys. All tabs look the same.
- Ribbon buttons are round icon buttons – icon in a white circle with a thin grey frame, hover lights the circle, switched-on buttons are a primary-coloured circle with white lines, Open primary, Save accent; plain line icons without the accent fill. The quick guide shows the same round icons (`RibbonButton.CircleSize`); help and info on the band are round too (light ring `Band.CircleBorder`, white lines, the circle fills on hover).
- Icons: lines in one ink colour (primary, light blue-grey in dark mode), an accent fill (`Icon.Tint`), red only for removing (Delete, Exit). The `Icon.Color.*` keys stay as meanings, the theme maps them to one colour.
- Content: light grey behind the pages (white pages with a thin frame and soft shadow); Organize and the page sidebar show every page on a white card that takes the page's shape (a page rotated to landscape lies on a landscape card), the selected (or active) card is filled in the primary colour with a white number.
- Buttons: the main action is accent-coloured with primary-coloured text ("Open PDF…" on the start page, the default button of every dialog, `Button.primary` / `Button[IsDefault=True]`), all others white with a grey frame. Lists are white cards with a primary-coloured selected row; counts sit in grey pills. Fluent's accent is the primary colour too (focus lines, check boxes, progress).
- Status bar light grey; the active mode is a filled primary-coloured button with white text.
- Colours are roles in `App.axaml` (`Band.*`, `Ribbon.*`, `Icon.*`, `Card.*`, `Selection.*`, `Primary.*`, `Button.*`, `Text.*` …) for Light and Dark; styles in `Styles/Controls.axaml` (plain controls) and `Styles/Ribbon.axaml`. Dark mode keeps the primary band and the accent, with dark surfaces tinted by the primary hue.
- `ScreenshotTests` renders start page, reading, Organize and some dialogs in both themes; with `EASYPDF_SCREENSHOTS=<folder>` it saves them as PNG for review.

**Commands** (defined once in `MainViewModel`; shortcut modifier = Ctrl on Win/Linux, Cmd on macOS via `PlatformHotkeyConfiguration.CommandModifiers`):
- File: Open, Recent files, Save, Save As, Concatenate, Extract selected pages, Split, Close, Exit
- Edit: Undo, Redo, Insert before/after, Replace pages…, Rotate… (dialog), Rotate left/right (quick), Delete pages, Select all
- View: Reading/Organize mode, First/Last page (Ctrl/Cmd+Home/End, both modes), Fit Height/Width/Best, Zoom in/out, Zoom 100 %
- Settings: Settings… (Ctrl/Cmd+,)
- Help: About <App>

**Menu bar:** `NativeMenu.Menu` on the MainWindow + `NativeMenuBar` in the layout → on macOS the system menu bar at the top of the screen (the in-window bar hides itself automatically), on Windows/Linux in the window. Menus:
- **File:** Open… · Open Recent ▸ (up to 10 entries, "Clear list") · Save · Save As… · Merge PDFs… · Extract Selected Pages… · Split Document… · Settings… · Close · Exit (macOS: Settings… and Quit in the app menu, per Mac convention)
- **Edit:** Undo · Redo · Insert Pages Before/After Current… · Rotate Pages… (Ctrl/Cmd+Shift+R) · Rotate Left/Right (Ctrl/Cmd+L / Ctrl/Cmd+R) · Delete Pages · Select All
- **View:** Reading Mode / Organize Mode · Fit Height / Fit Width / Fit Best · Zoom In / Zoom Out / Actual Size (100 %)
- **Help:** About <App>… (on macOS also in the app menu "<App> ▸ About <App>", per Mac convention)

**Ribbon (own control):** tabs as `TabControl` with its own `ControlTheme`; `RibbonGroup` = items panel with caption below; `RibbonButton` large/small; ribbon collapsible (double-click a tab, Ctrl+F1 or the arrow button at the right end of the tab row; a single click on a tab opens it again). An **ⓘ Info button** at the right end of the tab row opens About.
- **Narrow windows, like Office** (`Controls/Ribbon/RibbonLayout.cs`): a tab's groups sit in a `RibbonGroupsPanel`, each group's controls in a `RibbonGroupPanel` (a large button fills a column, small buttons and other controls are stacked three to a column – so the XAML lists a group's buttons flat, no nested StackPanels). Each `RibbonGroup` has a `Reduction`: 0 as designed, 1 large buttons shown small (they flow into the three-row columns), 2 icons only (label stays in the tooltip), 3 collapsed to one button "<Caption> ▾" that opens the group at full size below it (the controls move into a flyout and keep their DataContext; choosing a command closes it). The panel tries, from the right, level 1 for all groups, then 2, then 3 – each only where it makes the group narrower – and afterwards gives leftover room back from the left. Widths per group and level are cached and measured again when a group's size changes (e.g. another language). Fits at the window's minimum width of 700 px; collapsing only happens below that or with longer texts.
- **Home:** File (Open with recent-files dropdown, Save, Save As, **Merge…**) · Pages (Insert, **Rotate…** large button, Delete) · History (Undo, Redo)
- **Organize:** Mode (Reading/Organize toggle) · Pages (Insert, **Rotate…**, Delete) · Combine (**Concatenate…**) · Output (Extract selected, Split) · Selection (Select all)
- **View:** Zoom (Fit Height/Width/Best, zoom combo 25–400 %, Zoom in/out)
- The ribbon offers **every command of the menu bar** (a test checks it): Pages has *Blank page before / after*; View has Navigate (First page, Last page, Go to page…), Panels (Thumbnails, Collapse ribbon) and Application (New window, Close, Exit, Settings…); the Recent dropdown ends with "Clear list"; the **?** button opens the quick guide.

**Menu bar can be switched off** (Settings ▸ "Show the menu bar", Windows and Linux only, default off because every command is in the ribbon – macOS keeps its global menu bar and the option is hidden): `AppSettings.ShowMenuBar`, applied to all windows at once. The keyboard shortcuts are window key bindings and keep working without it.

**Reading view (`HorizontalPageView`):** pages side by side, mouse wheel → horizontal (trackpad Delta.X used natively), Shift+wheel vertical, arrow/PageUp/PageDown/Home/End, position kept on relayout. Control inside a `ScrollViewer`, extent = full layout width; `Render` draws only pages in the viewport. Status bar: mode switch (Reading/Organize, active mode filled in the primary colour – shows the mode whichever ribbon tab is open) + "Page x / y" + zoom out / zoom % with a menu of steps and fit modes / zoom in; during save/extract/split additionally what is running ("Extracting 12 pages…"), progress bar, percentage and Cancel.

**Free zoom:** besides FitHeight/FitWidth/FitBest a `Custom` mode with a percentage (100 % = real size at screen DPI). Ctrl/Cmd+wheel and trackpad pinch (Avalonia pinch gesture) zoom **around the mouse position**; Ctrl/Cmd +/−/0 (0 = 100 %); zoom combo in the ribbon. While zooming, cached bitmaps are shown stretched and re-rendered in the background.

**Swiping:** in the reading view the left mouse button drags the content (it follows the pointer, also vertically when zoomed in); released with speed it glides on and slows down exponentially (speed from the last 100 ms, up to 15 px/ms, time constant 650 ms, so the glide distance grows with the swipe speed; advanced once per displayed frame via `RequestAnimationFrame`); a click, the wheel or a key stops it. A press that moves less than 6 px stays a click (marks/unmarks the page with the red frame, decided on release).

**No outline sidebar:** most PDFs have no bookmarks, so such a panel would usually be empty. Bookmarks are still kept when saving; `PdfSource.ReadOutlineAsync` remains in Core (used by the save tests).

**Page sidebar:** in Reading mode a narrow column of page thumbnails on the left (`ThumbnailGrid` with `IsNavigator`): the page being read is highlighted and kept in view, a click or the arrow keys jump to a page. Hidden in Organize mode; switched on/off via View ▸ Panels ▸ Thumbnails / Ctrl/Cmd+T, remembered in `AppSettings.PageSidebarVisible`.

**Command line:** `<App> [file.pdf ...]` – one PDF is opened in Reading mode; several PDFs open the Concatenate dialog with all files in the given order (`ConcatenateContext.InitialFiles`), where the order can be changed before OK. Relative paths are resolved, missing files reported (`MainViewModel.OpenStartupFilesAsync`). The Linux desktop entry passes several files (`%F`).

**Context menu:** Windows: static verbs under `HKCU\Software\Classes\SystemFileAssociations\.pdf\shell` in the MSI (per-user install) – "Read" with `Player` and `--read` (several selected files: one window each), "Merge" with `Player` and `--merge`. Explorer starts one process per selected file (verified); for "Read" each hands its file to the running instance (`InstanceChannel`), and windows being filled count as taken so near-simultaneous requests do not land in the same empty window. For "Merge" `MergeInstances` lets the first process collect the others' files (named mutex + named pipe, 1 s quiet time) and show one Concatenate dialog. Static verbs cannot show an entry only for several files; that needs a native shell extension (IContextMenu/IExplorerCommand), and the new Windows 11 menu needs a packaged app. Linux: Dolphin service menus (Merge with `X-KDE-MinNumberOfUrls=2`) and Nemo actions (Read `Selection=notnone`, Merge `m`), both with `%F`. macOS: `CFBundleDocumentTypes` (Open With) + `FileActivatedEventArgs`.

**Several documents:** one document per window, all windows in one process (`WindowManager`). A file opens in the current window if it is empty, otherwise in a new window (same size, offset by 30 px; maximized if the current one is). A file that already is the document of a window is not opened twice – that window comes to the front. This applies to Open, Recent, drag & drop (several dropped files: one window each), Concatenate without the current document, and files from outside. File ▸ New Window (Ctrl/Cmd+N) opens an empty window; Close closes the document and, if other windows are open, the window; Exit (and Quit on macOS) closes all windows, asking about unsaved changes one by one (Cancel stops). Saving onto a file another window reads from is refused with a message (its handle would block the replace). **Single instance:** a later start hands its command line to the running process (`InstanceChannel`: named mutex + named pipe, current user only) and exits; on Windows it calls `AllowSetForegroundWindow` so the running process may come to the front. Files from outside go to the most recently active empty window; otherwise a single file starts at the active window (→ new window or the window that has it), several files/`--merge` and a start without files get a new window (so the Concatenate dialog does not offer the active window's document). Settings (recent files, language) are shared; the menus of all windows are rebuilt on changes; the window closed last saves its geometry. Both pipes carry one message of lines that the receiver confirms with one byte (`PipeMessage`); only the confirmation counts as handed over. On Linux and macOS .NET builds named pipes on Unix sockets, where a client can connect to the backlog and write although the server closes its pipe without accepting it – without the confirmation such a file was silently lost (Windows pipes accept one client per instance, so it never happened there). Tabs instead of windows were considered and postponed (bigger refactor of `MainViewModel`/`MainWindow`).

**Organize mode (`ThumbnailGrid`):** virtualized grid (`ItemsRepeater` + `UniformGridLayout`), thumbnails ~160 px rendered lazily at low priority. Selection: click, Ctrl/Cmd+click, Shift range, Ctrl/Cmd+A. **Drag & drop:** pointer-based inside the control (insertion marker, auto-scroll at the edge), drop → `EditableDocument.Move` (undoable). Double-click → Reading mode at that page. **Thumbnail size:** View ▸ Zoom in/out, Actual size (= standard size), the zoom box and Ctrl/Cmd+wheel change the thumbnail size in Organize mode (50–300 %, remembered in `AppSettings.ThumbnailZoomPercent`); the fit modes are disabled there. Dropping PDF files from the file manager onto the window → open.

**Page context menu (right-click, identical in Reading AND Organize mode):** shared `PageContextMenu`, bound to commands with the clicked page as parameter (in Organize mode a right-click on an unselected page selects it first):
- **Insert before this page…** / **Insert after this page…** → file picker (PDF, multi-select) → `InsertFrom(files, index / index + 1)`, one undo step; then jump to / select the first inserted page
- **Replace pages…** (Edit menu, ribbon *Replace*, context menu) → dialog: which **consecutive** pages (current / selected / range; gaps are refused), replaced by another window's document (web version: another tab's; copied with its unsaved changes) or a PDF file (picker), all or a range of its pages → `EditableDocument.Replace(first, count, document, pages)`, one undo step; the new pages take the place of the first replaced one (only consecutive pages; bookmarks to removed pages are treated like Delete)
- Rotate left / Rotate right · Delete page(s) · Extract page(s)… (Organize mode: selection)

**Rotate dialog (`RotateDialog`, opened by the ribbon button "Rotate…" / Edit ▸ Rotate Pages…):** modal, two separate groups:
- **Direction** (radio buttons, each with a small icon): 90° clockwise (default) · 90° counter-clockwise · 180°
- **Pages** (radio buttons): All pages · Current page (default; in Reading mode = page at the left edge, in Organize mode = focused thumbnail) · Selected pages (only enabled in Organize mode with a selection) · Range: text box, e.g. `1-3, 5, 8-10`, checked live by `PageRangeParser` (error text below the box, OK disabled while invalid; page numbers = current order).
- OK → `EditableDocument.Rotate(pages, angle)` as **one** undo step; Cancel does nothing. The dialog remembers the last direction during the session.
- The quick actions Rotate left/right stay in the page context menu (single page / selection, no dialog).

**Concatenate (`ConcatenateDialog`, ribbon "Concatenate…" / File ▸ Concatenate Files…):** modal dialog with a file list:
- Add files (picker, multi-select) or drop them from the file manager into the list; Remove; reorder via Move up/down buttons and drag & drop inside the list; Sort by name.
- Each row shows file name, folder and page count (read via PDFium on the worker when added; unreadable/password-protected files are marked red and block OK).
- Checkbox **"Include current document"** (only if one is open, default on): adds it as a movable entry "(current document)" with its *current* edits.
- Button **"Open documents"** (only if other windows have a document when the dialog opens; web version: other browser tabs, see "Documents of other tabs on the web"): one click appends the documents of all other windows (named as in their title) that are not listed yet; unwanted ones are removed from the list (no menu, to keep it simple). An entry is a *copy* of that document as it is at that moment, with its unsaved edits (`EditableDocument.CopyAsync` opens its files again), so it does not depend on the other window staying open or unchanged. The row says "Open in another window" (and "with its unsaved changes" if it had any).
- OK (needs ≥ 2 entries) → result opens as a **new, unsaved document** "Concatenated.pdf" in the viewer (= `EditableDocument` with several sources, so you can still reorder/rotate/delete before saving); Save asks for a file name (Save As). If the current document has unsaved changes and is *not* included, the unsaved-changes prompt comes first.
- Saving: the first file in the list is the base for `PdfSaver` (its bookmarks are kept), the others are imported. Large files are not loaded into memory (streaming sources, `FPDF_SaveAsCopy`).
- Difference to "Insert before/after": Insert adds files into the *open* document at a page; Concatenate builds a new document from a list of files.

**Extract / Split:**
- *Extract selected pages…*: Save dialog → new PDF with the selected pages (current order + rotation). The document itself stays unchanged.
- *Split document…*: `SplitDialog` with options "every N pages" or "at selected pages (each selected page starts a new part)"; folder picker; file names `<name>_part01.pdf` …; summary afterwards.

**Reduce file size** (File menu, ribbon Home ▸ File and Organize ▸ Output): `ReduceSizeDialog` with three levels (High 200 dpi / JPEG 85, Medium 150 dpi / 75 – default, Small 100 dpi / 60; the last choice is preselected during the session) → save dialog (`<name>_small.pdf`) → the document is saved there with `ImageReduction` and the window shows the new file (like Save As; the original stays unchanged) → message with old and new size.
- `ImageReducer` (Core) works on the file `PdfSaver` has just written: pdfium finds every picture and the lowest resolution it is shown at (also inside form objects; a picture used on several pages is one stream), Skia scales it to the target and encodes it as JPEG (grey stays grey), the new pictures are appended as an incremental update under the old object numbers, then pdfium writes the file once more (the old data is dropped). **The page contents are never generated again**: replacing pictures with `FPDFImageObj_LoadJpegFile` + `FPDFPage_GenerateContent` would let pdfium's content generator rewrite the page, and it turns spot-colour boxes (as in the demo PDF) black and drops their text. Stamps, texts and OCR text layers are safe with `FPDFPage_GenerateContent`: they only add new objects, which pdfium writes into a stream of their own, and it leaves the unchanged streams as they are (checked by `OverlayContentTests`).
- Left alone: pictures at most 1.25 × the target, with a mask/transparency (rendered with `FPDFImageObj_GetRenderedBitmap` and checked for see-through pixels; pictures it cannot render, e.g. turned by their matrix, count as masked), 1-bit scans (CCITT/JBIG2 are smaller), indexed/spot colour pictures, pictures smaller than 64 px, JPEGs that would not be smaller, encrypted files.
- An 8.3 MB manual with photos: 5.2 / 3.8 / 3.2 MB in about 1.5 s.

**Black out / white out** (ribbon Home ▸ Redact, Edit menu): two tools that work the same way, one draws black boxes, the other white ones (as if the paper were empty).
- **Workflow:** the tool stays on until Esc, the button again or another tool. In the reading view a drag that starts on a word selects text like the select-text tool and covers the selected words line by line (a band per line, 1 pt / 15 % of the height larger than the words); a drag elsewhere draws a box around any area (pictures, scans); a double click covers one word. With the select-text tool and a selection, the button covers that selection instead of switching the tool on. Without a tool, boxes are selected, moved, resized (freely) and deleted like stamps. Every step is undoable. Boxes are drawn opaque, white ones with a dashed outline on screen only. Words under a box can no longer be selected or copied.
- **Model:** `RedactionOverlay { Id, Rect (page space), Fill }` in `PageEntry.Redactions`, ids from the same counter as stamps/texts.
- **Saving – the content is removed, not hidden** (`PageRedactor`, runs after stamps/texts, so they are blacked out too, and before the OCR text layer, whose words under a box are left out): every top-level page object whose bounds reach into a box is removed (text, also invisible, pictures, paths, shadings, form objects). What these objects showed outside the boxes is put back as a picture of their area (rendered before removing, without annotations, boxes painted into the pixels; lossless at 300 dpi, or JPEG at the scan's own resolution, 150–300 dpi, where pictures were removed; one picture for the whole page if the areas cover most of it). Their characters outside the boxes come back as invisible text (loose char boxes, writing direction from the char matrix), so the page stays searchable. Objects that do not touch a box stay untouched (vector, real text). The boxes are written as filled rectangles on top. Annotations reaching into a box are removed; a form field also loses `/V` and its appearance (the field itself stays in the AcroForm, pdfium cannot remove it).
- **No leftovers in the file:** pdfium leaves out objects of the opened file that nothing refers to any more (checked: `FPDF_SaveAsCopy` walks the references), but writes every object created in the session, used or not – imported pages (Insert, Concatenate, Extract), stamps. So a document with boxes is written a second time (`PdfSaver.DropUnusedObjectsAsync`: open the written file, save it again; then all objects are old ones). `RedactionTests` decompress all streams of the result and look for the covered text (also hex-encoded), the original picture, comments and form values.
- **Warning before overwriting:** Save (also Save As or Reduce Size onto the document's own file, and Save from the unsaved-changes question) with boxes asks first: `RedactedSaveDialog` with "Save a copy…" (default button, suggests `<name>_redacted.pdf`, the window then shows the copy), "Overwrite" and Cancel ([X] = Cancel); "Don't ask again" switches it off. Setting `AppSettings.WarnOverwriteRedacted` (on by default), checkbox in Settings ▸ Black out and white out.
- **Limits:** a redacted page's text that comes back as invisible text uses Helvetica/WinAnsi (other characters become "?", as for OCR); bookmarks, document metadata, page thumbnails (`/Thumb`) and field values of fields whose widget is a separate kid are not touched.

**Copy area / paste area** (ribbon Home: "Copy area" in the Redact group, "Paste" in the History group; Edit menu, Ctrl/Cmd+V): copy any rectangle of a page and place it elsewhere, opaque – only as a picture, no cut.
- **Workflow:** the copy-area tool draws a frame (like a redaction box); on release the area is copied as it is shown – the page with its unsaved stamps, texts and boxes, at 300 dpi (lower for areas above ~40 MP), upright as in the view – and hangs at the pointer at once; a click places it in the size it was copied (reduced only to fit the page). Esc keeps the copy for Paste area later; the copy is shared by all windows of the process (`AreaClipboard` in `WindowManager`), not put on the system clipboard.
- **Model:** a pasted area is a `StampOverlay` with `CoversContent = true`: moved/resized/deleted like a stamp, drawn above stamps and texts, below boxes. `PageEntry.RemovalAreas` = redactions + pasted areas as white boxes.
- **Saving:** the content under a pasted area is removed only when saving (so it can still be moved): `PageRedactor` runs over `RemovalAreas`, then the pasted pictures go on top; boxes lying over a pasted area are painted into its pixels. Its text is not searchable (it is a picture); the save warning stays limited to blacked-out content.

**Recent files:** stored in `AppSettings` (max. 10, most recent first, missing files are removed when chosen with a hint). Available in File ▸ Open Recent and as a dropdown on the ribbon Open button. The same settings file also remembers window size/position, ribbon collapsed state and last zoom mode.

**Help / About:** `AboutWindow` (modal) with app icon, the shown name, the brand's company logo on the right (`branding/<id>/icons/company-logo.svg`, drawn as vector graphics by `Styles/SimpleSvg.cs` so it has no steps or blur at any display scaling; hidden without one; tooltip = publisher), **version** (`AssemblyInformationalVersion` = `<Version>` from `Directory.Build.props` + git commit hash, which the .NET SDK adds automatically), build date, .NET runtime version, OS + architecture, Avalonia version, PDFium build (version of the `bblanchon.PDFium` package, written into the assembly at build time), license (MIT) and a link to the GitHub repository. Button **"Copy version info"** (clipboard, handy for bug reports) + Close.

**Settings (`SettingsWindow`, File ▸ Settings… / macOS: <App> ▸ Settings…, Ctrl/Cmd+,):** small dialog, changes apply immediately (no restart) and are saved in `AppSettings`:
- **Appearance:** System (default) · Light · Dark → sets `Application.RequestedThemeVariant`.
- **Language:** System (default: German if the OS UI language is German, otherwise English) · English · Deutsch → sets the culture of `Loc`; all texts incl. menu bar, ribbon, dialogs and status bar switch live.
- Room for later options (e.g. default zoom mode, thumbnail size).

**Dialogs:** `StorageProvider` file/folder pickers (native on all OSes); prompt for unsaved changes on Open/Open Recent/Close/Exit.

## UX – how the app is meant to work

### Overall idea
The app is a lean tool for **reading and reorganizing** PDFs, not a full PDF editor. The user opens a document, reads it page by page side by side, and rearranges it: changing page order, rotating, deleting, inserting other files, concatenating several files, extracting or splitting parts. All changes are first made only to the document shown on screen and can be undone at any time. The file on disk changes only on **Save**. That makes editing fast (even with large files) and safe.

### Getting started
Without an open document, the window shows a start page with "Open PDF…", "Concatenate…" and the list of recently opened files. A PDF can also simply be dragged onto the window. Opening is fast even for very large files, because only the pages currently visible are loaded; the rest follows in the background.

### Reading (Read mode)
The pages sit side by side and are scrolled horizontally: mouse wheel, touchpad, arrow keys, PageUp/PageDown (page by page), Home/End. By default each page fills the window height; alternatively fit to width, best fit, or free zoom (Ctrl/Cmd + mouse wheel, pinch on the touchpad, zoom field). Zooming centers on the mouse position, so the spot you're looking at stays put. The status bar always shows "Page x of y"; a click on it lets you jump to a page directly. The page sidebar on the left shows the thumbnails in one column that fills its width; dragging its right edge makes it wider (up to half the window), so the thumbnails are rendered larger; the width is remembered.

**Find text** (Ctrl/Cmd+F, Edit > Find…, ribbon "Find"): a search bar above the pages. Typing searches the whole document at once – recognized words (not saved yet) or else the text layer of the file, without blacked-out words, as when copying text. Words are joined with single spaces, so a phrase is also found across line ends; case does not matter. All hits are highlighted in the reading view, the current one in orange; the first hit from the current page on is shown (in Organize mode the app switches to reading). Enter/F3 = next hit, Shift+Enter/Shift+F3 = previous (wrapping around), Esc closes the bar and removes the highlights. Pages are searched from the current page on, so the first hit appears quickly; an edit searches again. A document without any text says so and points to text recognition. Logic: `Core/Ocr/TextSearch` (hit boxes cut to the matching letters), state in `MainViewModel.Search.cs`, drawing in `HorizontalPageView.Search.cs`.

### Organizing (Organize mode)
One click (or Ctrl/Cmd+2) switches the content area to a grid of page thumbnails. Here the user sees the whole document at a glance and works with it:
- **Select:** click, Ctrl/Cmd+click for several, Shift+click for a range, Ctrl/Cmd+A for all.
- **Reorder:** drag selected pages with the mouse to the new position; a marker shows where they will land. Several pages are moved together and keep their relative order.
- **Rotate, delete, extract** apply to the selection.
- A double-click opens the page in Read mode. Selection and current page carry over between the two modes.
Pages that come from inserted or concatenated files carry a small colored marker so it's clear where they came from.

### Right-click on a page
In both modes, a right-click on a page opens the same context menu:
- **Insert before this page… / Insert after this page…** – a file dialog opens; the chosen PDF(s) are inserted completely at that point. Afterwards the view jumps to the first inserted page.
- Rotate left / right (immediately, without a dialog), Rotate… (opens the Rotate dialog)
- Extract page(s)…, Delete page(s)
- Switch to the other mode at this page

### Rotating
For quick corrections there is "Rotate left/right" (context menu, Ctrl/Cmd+L / Ctrl/Cmd+R). For targeted rotations, the ribbon button **Rotate…** opens a dialog with two independent choices:
- **Direction:** 90° clockwise, 90° counter-clockwise, or 180°.
- **Which pages:** all pages, the current page, the selected pages (in Organize mode), or a range such as "1-3, 5, 8-10". The range is checked as you type; invalid entries (e.g. a page that doesn't exist) are explained right away and the button stays disabled.
The result is visible immediately and can be undone in one step.

### Concatenating
**Concatenate…** builds a new document from several PDF files. In the dialog the user collects files (via the file dialog or by drag & drop), puts them in the desired order, and removes ones they don't want. For each file the page count is shown; files that can't be read (damaged, password-protected) are marked. Optionally the currently open document, including its unsaved changes, can be included.
The result opens as a new, not yet saved document. The user can then rearrange it further (move pages, rotate, delete) and finally save it under a new name.
Difference to "Insert": Insert puts files *into the open document* at a specific page; Concatenate builds a *new* document from a list of files.

### Extracting and splitting
- **Extract:** the selected pages are saved as a new PDF; the open document stays as it is.
- **Split:** the document is broken into several files, either every N pages or at chosen pages. The user picks a target folder; the parts are numbered automatically (e.g. "Manual_part01.pdf").
- **Reduce file size:** scans and pictures with more resolution than needed are scaled down; the smaller copy is saved under a new name and shown, text and drawings stay as they are.

### Attachments and electronic invoices
Files embedded in the PDF (document-level attachments, `/EmbeddedFiles`) are read with pdfium (`PdfSource.ListAttachmentsAsync`, `Core/Pdfium/PdfAttachments.cs`); they belong to the document's file (`BaseSource`) and saving keeps them (`PdfSaver` works on a copy of that file).
- **Electronic invoice:** an attachment named `factur-x.xml`, `zugferd-invoice.xml` or `xrechnung.xml` (any case; the names ZUGFeRD 1/2, Factur-X and XRechnung prescribe) is the invoice XML. A bar above the pages says so, with **Open XML** (system program) and **Save XML…** (suggested as `<PDF name>.xml`, next to the PDF); [X] hides it for this document. There is no viewer of our own: the official visualisations are XSLT, which does not work under Native AOT, and the system's XML program or a dedicated e-invoice viewer does the job better.
- **Attachments…** (ribbon Home ▸ File, shown only when there are attachments, since the tab has no room to spare; menu File, enabled only then) lists all embedded files with size, the invoice marked and preselected: **Open** or **Save…**.
- **Open** writes the file to a temporary folder (`<temp>/<App>-attachments/<random>/`) and hands it to the system (`Launcher`). Only harmless types open (`PdfAttachment.CanOpen`: XML, PDF, text, CSV, JSON, pictures, Office documents without macros); programs, scripts and the like can only be saved, the dialog says why. Names from the PDF are cleaned (`SafeFileName`: no folders, no invalid characters).

### Undo and saving
Every action (move, rotate, delete, insert) can be undone and redone (Ctrl/Cmd+Z / Ctrl/Cmd+Y). As long as there are unsaved changes, the window title shows a dot and the status bar shows a notice. **Save** overwrites the original file safely (written to a temporary file first, then replaced); **Save As** creates a new file. Bookmarks, metadata and form fields of the original document are kept. When opening another file, closing or quitting with unsaved changes, the app asks first.

### Operation: menu, ribbon, shortcuts
Every function is available in three ways that always behave the same:
- **Menu bar** (File, Edit, View, Help) – on macOS, as usual, at the top of the screen.
- **Ribbon** with the tabs *Home* (open, save, concatenate, insert, rotate, delete, undo), *Organize* (switch mode, page actions, concatenate, extract/split, select all) and *View* (zoom, fit modes). The ribbon can be collapsed to gain space.
- **Keyboard shortcuts**, on macOS with Cmd instead of Ctrl.
Disabled actions (e.g. "Save" without changes) are greyed out in all three places at once.

### Help and information
**Quick guide** (Help ▸ Quick Guide, **F1**, the **?** button at the right edge of the ribbon, "New here?" link on the start page): a non-modal window (`QuickGuideWindow`) with one short section per function, each with its ribbon icon – open, read, organize, change pages, stamps, text, OCR, concatenate, extract/split, reduce size, print, send by email, undo/save, settings/help. Texts `Guide<Topic>Title/Text` in English and German, "{0}" stands for Ctrl (⌘ on macOS). New functions get a section there.
The Help menu and the **ⓘ** button at the right edge of the ribbon open the *About* dialog: program name, version number including build information, runtime environment, operating system, license and project link. "Copy version info" puts all of this on the clipboard, e.g. for bug reports.

### Settings
Rarely needed options live in a small **Settings** dialog (the gear in the brand header left of the ?, reachable from every tab; View tab ▸ Settings…; File ▸ Settings…, on macOS as usual under <App> ▸ Settings…, shortcut Ctrl/Cmd+,), so they don't clutter the ribbon:
- **Appearance:** follow the system (default), always light, or always dark.
- **Language:** follow the system (default), English, or German.
- **PDF files:** "Open PDF files with <App>" makes the app the default PDF program, with a status line ("<App> is the default program …"). Linux: `xdg-mime default <package>.desktop application/pdf` (per user, only with the installed .deb); macOS: LaunchServices for the bundle id of the running .app; Windows does not let programs do this themselves (since Windows 8), so the button opens the app's page in Settings ▸ Default apps (`ms-settings:defaultapps?registeredAppUser=<name>`) – the MSI registers the app for .pdf (ProgId `<name>.Document`, OpenWithProgids, Capabilities, RegisteredApplications) and its dialog "Preferences" has the check box "Use … as the default program for PDF files" (Finish opens the same page; hidden when it is the default already). Linux and macOS have no installer that could ask: the app asks once at the first start (`DefaultAppPrompt`, `AppSettings.DefaultAppAsked`), only if it is installed and not the default yet.
Changes take effect immediately, without a restart, and are remembered for the next start.

### Appearance and feedback
- Light or dark according to Settings (by default following the operating system).
- Long operations (saving large files, concatenating, splitting) show progress with a Cancel option; reading remains possible meanwhile.
- Errors appear as a dialog with a short explanation and optional technical details; minor hints (e.g. "File no longer exists, removed from the list") appear briefly in the corner and disappear by themselves.
- Available in English and German; further languages only need an additional translation file.

## Web version
The same user interface in the browser, with the comfort of the desktop (drag & drop of pages and of files from the
operating system). Decisions: pdfium runs on the **server**, the user interface is the shared Avalonia code compiled to
**WebAssembly**, both talk plain HTTPS/REST (no WebSocket, no Blazor). Hosting: one container per brand (Docker/Kubernetes).

**Projects.** `EasyPDF.UI` holds the whole user interface (`MainView` = everything of one document as a `UserControl`).
The desktop program `EasyPDF.App` shows it in a `MainWindow` (window size, closing, title) and is otherwise unchanged; the
browser program `EasyPDF.Browser` shows it as the page (`ISingleViewApplicationLifetime`). `EasyPDF.Remote` is the contract
(source-generated JSON) and the browser-side client; `EasyPDF.Server` the server. Everything is .NET 11; Avalonia 12 brings the WebAssembly builds of Skia and HarfBuzz for
the Emscripten of .NET 9/10 only, so the browser program takes them from SkiaSharp 4 and HarfBuzzSharp 14 (built for .NET
11's Emscripten) and drops Avalonia's (`EasyPDF.Browser.csproj`).

**Seams.**
- `IPdfSource` (Core): what the views need of a document (page count and sizes, rendered pages, words of the text layer,
  attachments). `PdfSource` is the local pdfium implementation, `RemotePdfSource` asks the server; `PageEntry` and
  `EditableDocument` only know the interface, so editing, undo/redo and the views run unchanged in the browser.
- `PdfBackend.Current` (Core): everything that opens or writes whole files (open, blank page, save, extract). Local by
  default; the browser program sets `RemotePdfBackend`. Files are still addressed by path: an upload gets the virtual path
  `/remote/<id>/<name>`, a target chosen in the save dialog `/save/<id>/<name>` (written through the browser's file API
  or as download). Since a browser cannot write back to an uploaded file, *Save* always asks for the target there
  (`IPdfBackend.CanOverwrite`).
- `PlatformFeatures` (UI): `Files` turns picked/dropped files into those paths (upload in the browser), `Current` says which
  commands the platform offers. Ribbon buttons, shortcuts and context menu entries of commands the web version does not
  offer yet are hidden (and a ribbon group whose buttons are all hidden); the list is `EasyPDF.UI/Services/WebCommands.cs`.
- Dialogs: a dialog's content is a `DialogView` (`UserControl` with title, size and `Close(result)`). On the desktop a
  `DialogWindow` shows it (the old dialog class names remain as these windows), in the browser `DialogLayer` shows it as
  an overlay on the page; `MainView.ShowDialogAsync` picks one. While an overlay is open the page's shortcuts do nothing.
  Converted so far: message, unsaved changes, rotate, insert range, merge, split, replace pages, reduce size,
  attachments, settings, quick guide,
  About; the others are windows only. On the web the settings offer theme and language and are kept in the browser
  (`localStorage`, `AppSettings.Saved`), the quick guide shows the topics of what is offered, About lists the brand's
  web address (`WebAppUrl`, also on the desktop).

**Overlays.** What is placed on a page and not saved yet (texts, black out/white out boxes, pasted areas, later stamps
and recognized words) travels with the page list (`RemotePage`); placed pictures as their decoded pixels, so the server
needs no image decoder. The server checks counts, sizes and numbers before writing them with the same `PdfSaver`. The
browser has none of the system fonts: the preview of a text uses a fallback font, the saved file the real one.

**Server.** REST under `/api`: upload (`POST /files`, PDF header checked, opened with pdfium right away, all page sizes
returned), rendered pages as PNG (own codec in Core, `PngCodec`, so neither side needs a native image library), words,
attachments, content, `build` (writes a page list based on a file: the snapshot of `EditableDocument`), `extract`,
`blank`, `split` (ZIP), `ocr` (text recognition of a page: `RemotePdfSource` is an `IServerOcr`, so `PageOcr` asks the
server; one `RapidOcrEngine`, one page at a time, `OcrService`), `DELETE`. pdfium stays on its one worker thread; opened files are kept in a small LRU (`OpenDocuments`).

**Owners and retention.** Every file belongs to an owner: in mode `None` an anonymous session (random id in the cookie
`__Host-session`, `Secure`, `HttpOnly`, `SameSite=Strict`), in mode `Oidc` the signed-in user (`sub`, generic OpenID
Connect: Keycloak, Entra ID, …; optional group or role). On disk only a hash of either. File ids are random 128-bit
values and are only found together with their owner (otherwise 404). Files go 30 minutes after their last use, at the
latest after 8 hours, when the document is closed in the app (the last reference) and at every start; an expired
session takes its files with it. In mode `None` the browser keeps settings, stamps and templates itself (see below).

**Hardening** (as in the other web apps on the same host): security headers from the app (`SecurityHeadersMiddleware`:
CSP `default-src 'self'` with `'wasm-unsafe-eval'` only, no inline scripts; `nosniff`, `Referrer-Policy`,
`Permissions-Policy`; `frame-ancestors 'self'` instead of `X-Frame-Options`; HSTS for one year from the app itself, `UseHsts`); `X-Forwarded-*` only from loopback, the container
gateway (read from `/proc/net/route`) and configured proxies; changing requests need the header `X-App-Request` (CSRF; no
CORS); rate limits per client address (all API requests, uploads/writes); upload, per-owner and total quotas; render size
limit; a time limit per pdfium job; no details in error responses, no file names in the log; no server header; Kestrel
header timeout; data protection keys in the volume. Container: chiseled image without shell, non-root, `--read-only`,
`--cap-drop=ALL`, `no-new-privileges`, memory limit, self health check (`--healthcheck`).

**Stamps and templates on the web** (`BrowserLibrary`, `IUserLibrary`): the stamp and templates folders exist in the
program's memory as on the desktop, so gallery, editor, date field and templates work unchanged (their dialogs are
`DialogView`s). The user's own stamps and templates are kept as one ZIP file (`LibraryArchive`: `stamps/` with one level of
subfolders, `templates/`) in the site's IndexedDB, unpacked at start and packed again after each change (new, edited or
deleted stamp, edited templates); "Export/Import stamps and templates…" in the stamp gallery and the templates flyout
take the place of "Open folder" and move them to another browser or computer. The server sends `GET /api/library` (one
ZIP): the sample stamps (copied into the folder by `SampleContent` as on the desktop, the provided ones are remembered in
the settings) and the installation's shared folder (`Storage:SharedFolder`, `/data/shared`: `stamps/*.png` with one level
of subfolders, `templates/*.txt`; files above 5 MB are left out). Shared stamps show as gallery sections "Shared" and
"Shared – <subfolder>", can be used but not deleted, and an edited one is saved as the user's own; shared templates are
added unless the user has one of the same name and are kept only once changed. The web gallery's right-click menu also
deletes the user's own stamps (there is no folder to delete them in).

**Fonts on the web:** a browser has no system fonts, so the preview of placed text would fall back to the UI font. The
browser program brings Liberation Sans, Serif and Mono (Regular, SIL OFL, `EasyPDF.Browser/Assets/Fonts`), which have the
widths of Arial/Helvetica, Times and Courier, the standard fonts the text is written with (`TextMetrics.EmbeddedFonts`).

**Share on the web** (the Email button, labelled "Share" with its own icon): a browser cannot start the email program with an attachment (`mailto:` takes no files).
Instead the server writes the document as it is now (like saving, with bookmarks) and the browser hands it to the
system's share sheet (Web Share API, `navigator.share` with a file), where the user picks the email program (Outlook,
Mail, …), which opens a new message with the PDF attached, or another target (messengers, AirDrop). Where
`navigator.canShare` does not accept a PDF file (Linux, Firefox on the desktop) a message says so and Save as (download)
follows.
Browsers allow sharing only shortly after a click: if preparing took longer (`NotAllowedError`), a message says the
attachment is ready and its OK click shares the already fetched file.

**Desktop downloads on the web:** a header button (download arrow, only when the server has packages) opens a dialog
with the newest package of each kind from the server's downloads folder (`/api/downloads`, `/api/downloads/<name>`; only
files named like the installer scripts name them, never other files of the folder). A choice of the system (Windows,
macOS, Linux; those without packages disabled) shows its packages; the browser's system (`navigator.userAgentData.platform`
or the user agent) is preselected and marked "(your system)". The
packages are not in the image, so new installers need no new image (see BUILDING.md "Desktop downloads").

**About on the web:** instead of the build date of a program file it shows when the server was deployed (`/api/config`:
the time its program file was built, in the container the image build) with a badge "Containerized" or "Native"
(`DOTNET_RUNNING_IN_CONTAINER`, set by the .NET images).

**Start on the web:** the browser program is published with **WebAssembly AOT** (`RunAOTCompilation` in
`EasyPDF.Browser.csproj`): the code runs compiled, not on .NET's interpreter. The first visit downloads about 12 MB
(Brotli; the native module with all compiled code is 9 MB of it; without AOT it was about 8 MB); afterwards the files come
from the browser's cache (see "Caching on the web"). Mono's AOT compiler of .NET 11 aborts on `SkiaSharp.dll` and
`HarfBuzzSharp.dll` (assertion in `sgen-alloc.c`), so these two stay interpreted (`_AOT_InternalForceInterpretAssemblies`,
an item internal to the WebAssembly SDK: check it when updating .NET). Measured with `tools/web-timing` (Firefox
headless, local server, one run each):

| | interpreter | AOT |
|---|---|---|
| runtime ready (empty cache / second visit) | 1.2 / 0.9 s | 3.4 / 2.2 s (compiling the larger module) |
| program shown (first frame) | 8.5 / 8.4 s | 6.7 / 5.2 s |
| of that: Avalonia start, main view, first layout | 6.5 s | 2.5 s |
| dialogs, first / second opening | 360–970 / 210–680 ms | 150–360 / 85–200 ms |

Building a dialog is the small part (10–160 ms interpreted); most of the time is the first layout and drawing (styles,
control templates, text). Before the program shows itself it asks the server for its configuration first (on the first
visit that answer sets the session cookie), then for the downloads list and the library side by side; the videos are
looked up in parallel from the start. The library (`/api/library`, about 1.3 MB) has an ETag (hash of the ZIP, whose
entries carry the files' own times); the browser program keeps the last copy with its ETag in IndexedDB and asks with
`If-None-Match`, so a second visit gets "not modified" (.NET's `HttpClient` in the browser does not let the browser's own
cache revalidate). With everything cached a second visit transfers about 30 KB. Until the
program shows itself the page has a splash (`index.html`, `main.js`): spinner, "Loading … n %" with a bar from the
runtime's `onDownloadResourceProgress` (counts files, not bytes), then "Starting …" while .NET starts and the program asks
the server for its configuration, library and videos. Its language is the one of the settings in `localStorage`,
otherwise the browser's. The splash lies above the program's canvas and goes once the program has started.

**Caching on the web** (`StaticCaching`): the publish names every file of `_framework/` after a hash of its content
(`System.Text.Json.aucta6todm.wasm`), so a new .NET version or a change of the app gives changed files new names. Those
files are sent with `Cache-Control: public, max-age=2592000, immutable`: the browser keeps them for 30 days without
asking, afterwards it asks again (ETag) or loads them anew. Files whose names stay across versions (`index.html`, also as
the fallback for deep links, `_framework/dotnet.js` with the list of the hashed names, `main.js`, `interop.js`, styles,
Avalonia's scripts, videos) get `no-cache`: the browser asks every time and gets "not modified" (304) while they are
unchanged, so a new version is picked up on the next visit. The precompressed `.br` copies have ETags of their own. The
.NET runtime fetches its files with `cache: "no-cache"` by default, which would ask for each of them on every visit;
`main.js` turns that off (`disableNoCacheFetch`), the integrity check against the hashes stays on.

**Documents of other tabs on the web:** several windows are another browser tab each, every tab a program of its own,
so the merge dialog's "Open documents" asks the other tabs of the same site instead (`IBrowserTabs`, `TabExchange` in
`EasyPDF.Remote`, small JSON messages over the browser's `BroadcastChannel`, `interop.js`). When the dialog opens it asks
"who has a document?" and shows the button if a tab answers within 300 ms. A click asks each tab for a copy: that tab has
the server write its document as it is now, with its unsaved changes (`/api/build`, like Share), and hands back the new
file. Tabs of one browser share the session cookie (with sign-in: the user), so the asking tab may open it; it owns it
from then on (deleted when its entry or the merged document lets go of it; a copy that arrives after the asking tab gave
up waiting is deleted right away). A tab that was closed or could not write its document gives a row marked as failed. A
private window has a cookie of its own and is not reached (the channel is per browser profile anyway). Replace pages
lists the other tabs' documents the same way (asked when the dialog is shown, preselected unless the user already chose a
file) and copies the chosen one on OK; if the copy has fewer pages than the tab said and the range no longer fits, it
says so and the dialog stays open.

**Intentionally not in the web version** (hidden): several windows (another browser tab does the same), recent files (the files on the server are
temporary). Split writes the parts as one ZIP file (no folder to choose in a browser); attachments are saved
(downloaded), a browser cannot open them in a program of the system; only XML files (the e-invoice) open, as indented plain
text in a new tab (`/api/files/{id}/attachments/{n}?view=true`, under a sandbox CSP: nothing in the file runs).

## Stamps, text and templates

### Insert stamp
- **Idea:** ribbon button **"Insert stamp"** (Home tab, also in the page context menu) lets the user choose a picture from a **stamp folder** and place it on a page – e.g. scanned signatures, company stamps, logos, "Paid" stamps the user created themselves.
- **Stamp folder:** a normal folder containing PNG files (**PNG only**: a JPG has no transparency and can't hold the fields of a stamp; JPGs in the folder are not shown, a JPG renamed to .png counts as unreadable; the gallery, the editor and Settings say so in small print). The path is set in **Settings** (default: `Documents/<App>/Stamps`, created at program start). The user manages the pictures with the file manager (copy in, rename, delete). The program ships sample stamps (`src/EasyPDF.Core/Stamps/Samples/`, published as `sample-stamps/` next to the program, so every installer contains them); At program start (`SampleContent.Provide` in `App`) the stamp and templates folders are created and get every sample not provided before, also when the folder exists already (an uninstall keeps the user's Documents folder, so a reinstall finds the old folder); `AppSettings.ProvidedSamples` remembers what was provided ("stamp:<file>", "template:<id>"), so deleted samples don't come back and samples added in a later version still arrive. A file of the same name is never overwritten. Samples in a subfolder of `Stamps/Samples` go into a subfolder (name in the program's language: `Numbers` → "Nummern"); the gallery shows the pictures directly in the folder first and then one collapsible section per subfolder (one level deep), which stays open as the user left it while the window is open. **Number stamps** 00–20 (`Stamps/Samples/Numbers`, made by `tools/stamps/make_number_stamps.py`: flat red disc, thin white ring, white Barlow Bold number) mark an order on pages. They are 236 px with 300 dpi stored in the PNG: a stamp is placed in the size its stored resolution gives (PNG pHYs; `StampLibrary.ReadDpi`, 96 dpi without one), so they come in at 20 mm and stay sharp in print. Settings and the stamp gallery get an "Open folder" button.
- **Selection:** the button opens a dropdown gallery with thumbnails and file names of all PNG files in the folder (sorted by name; the folder is re-read each time the gallery opens, so new files appear without a restart). Unreadable files are skipped with a hint; if the folder is empty or missing, the gallery explains how to fill it.
- **Workflow:** choose a picture → the picture follows the pointer, centred on it, slightly transparent so that the page shows under it (80 % opaque, dashed frame), and over a page it replaces the pointer → click on the page places it (initial size based on the picture's resolution, limited to the page) → afterwards it can be moved, resized (keeping the aspect ratio) and deleted as long as it is not saved; undo/redo like every other action. Works in Read mode. PNG transparency is preserved (important for signatures/stamps on a white background).
- **Technical sketch:** the picture is loaded when placed and kept in the document model, so later changes in the folder don't affect placed stamps. Each `PageEntry` gets a list of overlays (`StampOverlay { image data, position, size in PDF coordinates }`), drawn on top of the page in the viewer and written on Save via PDFium permanently into the page content (`FPDFPageObjNewImageObj` + `FPDFImageObjSetBitmap` + `FPDFPageInsertObject` + `FPDFPageGenerateContent`).

### Stamps with fields
- **Idea:** a stamp can have dynamic content, first of all the date of a "Received" stamp: **stamp = picture + text fields with macros**. The macros are those of the text templates (`TemplateExpander`: `{$DATE:dd. MMM. yyyy}`, `{$USER}` ...). The text does not have to be searchable in the PDF.
- **Definition:** `StampDefinition`/`StampField` (`Core/Stamps`): area as a share of the picture (0 to 1, independent of its resolution), text, colour, uppercase, alignment, language. The font size follows from the height of the area. The language belongs to the stamp, not to the program; short month names come from a table of our own (`StampCulture`) because those of the system depend on the ICU version.
- **Stored inside the PNG** (`StampFile`, iTXt chunk `EasyPDF.Stamp` with JSON): a stamp stays one file that can be renamed, copied and passed on, and gallery, `ListImages` and `ProvideSamples` work unchanged. Rejected: a JSON file next to the picture (lost on rename, samples copy only pictures), SVG with placeholders (new dependency, external editor), picture + `TextOverlay` (standard fonts only, the two parts would have to stay together when moved). A program that rewrites the PNG may drop the chunk; then it is a plain picture again. Anything unusable in the chunk also leaves a plain picture.
- **Placing** (`StampComposer`, App): the text of the fields is drawn into the picture when it is placed (as an outline, so no colour fringes on the transparent picture; the program's font Barlow Bold; capitals fill the height of the area, a date doesn't change size with its month, too wide text is reduced). The result is an ordinary picture: moving, turning, covering and saving (`PdfSaver`) know nothing about fields. The date and page are those of the click and stay. The placed `StampOverlay` keeps what it was made from (`StampTemplate`). The gallery and the preview at the pointer show the stamp as it would look today.
- **Samples:** `Eingegangen.png` and `Received.png` (`tools/stamps/make_received_stamps.py`): the word in the picture, the date as a field. Both arrive in every language, because samples are copied as files.
- **Changing the date:** a double click on a placed, unsaved stamp whose fields show a date opens a calendar (`StampDateDialog`; closing it with [X] = cancel). The picture is made again from `StampTemplate`, one undo step, position and size stay. The stamp gets a new id, so the view's picture cache (by id) stays right across undo and redo. A saved stamp is part of the page and can't be changed.
- **Editor** (`Dialogs/StampEditorDialog`, surface `Controls/StampEditorCanvas`): opened from the gallery ("New stamp...", right click on a stamp: "Edit..."). Start from a PNG picture or an empty area (60 x 25 mm); optionally white becomes transparent (for a picture with a white background; `StampImage.WhiteToTransparent`). The picture dialog offers only PNG; any other file is refused with a message. Draw an area for a field, move and resize it, choose text with macros, colour, alignment, uppercase and language, with a live preview using today's date. Saving writes a PNG into the stamp folder (renaming removes the old file).

### Insert text
- **Idea:** ribbon button **"Insert text"** (Home tab, next to the stamp button, also in the page context menu). Click on a page, type, done – the text is a movable box like a stamp until it is saved.
- **Workflow:** choose the tool → the cursor becomes a text cursor → click on the page creates a text box at that spot with an inline editor (multi-line; Esc/click outside ends editing, empty text is discarded) → afterwards select/move/resize/delete exactly like stamps; double-click edits again. One undo step per create / edit / move / delete.
- **Model:** a second overlay kind next to `StampOverlay` in `PageEntry` (common base `PageOverlay` with `Id`, `Rect` in page space, `Rotation`): `TextOverlay { Text, FontFamily, FontSize (pt), Color, Bold/Italic }`. Same page-space rules as stamps (turns with the page, survives later rotation); the stamp code for hit testing, drag, resize handles and Delete is generalised, not duplicated.
- **Pen colour:** a colour button with a palette dropdown (plus "More colours…") in the ribbon group; the choice is stored in `AppSettings` (`TextColor`, also font size and font) and restored at the next start. Changing it with a text box selected recolours that box (undoable); without a selection it sets the colour for new text.
- **Saving:** text is written as real PDF text (`FPDFPageObjNewTextObj` / `FPDFTextObjSetText`, `FPDFPageObjSetFillColor`), so it stays selectable and searchable. Only the 14 standard fonts are certain to render everywhere, so the default is Helvetica/Times/Courier (WinAnsi, no CJK); non-Latin text would need an embedded font (`FPDFText_LoadFont` with a TTF from the system), not done so far.
- **Templates:** the **Templates** button offers text snippets from the **templates folder** (like the stamp folder: path in Settings, default `Documents/<App>/Templates`, "Open templates folder" in the flyout and in Settings). One UTF-8 text file per template: the file name without `.txt` is the name, the content the text (a final line break is ignored), so templates can also be written and shared with any editor; the flyout reads the folder each time it opens. "Edit templates…" edits them in a dialog and writes the result back as files (`TemplateLibrary.Save`: removed or renamed files are deleted, characters a file name can't have become `_`, the same name twice gets " (2)"). At program start the folder is created and gets the sample templates not provided before, in the language the program shows at that moment (`SampleContent`, texts in `Strings*.resx`: date, page x of y, draft, received; same mechanism as the sample stamps, a sample's id is the same in every language, so a deleted one doesn't come back in the other language). Choosing one places it like normal text: until the click the text itself follows the pointer in its real font, size and colour, its top-left corner at the pointer, with a dashed frame; over a page the pointer is hidden so it does not cover the first letters, and the preview shows the macros as they would be expanded on the page under the pointer. The click places it exactly where the preview was (`HorizontalPageView.TemplateTargetAt` computes both). Templates may contain **macros** that are expanded when the text is placed (and not later, so the placed text is plain): `{$DATE}` (current date, locale format), `{$DATE:yyyy-MM-dd}` (format), `{$TIME}`, `{$USER}` (OS user), `{$FILE}` / `{$FILENAME}` (document name), `{$PAGE}` and `{$PAGES}` (page number / page count at placement), `{$TITLE}`. Unknown macros stay as typed. The expansion lives in Core (`TemplateExpander`, pure function with a context object, fully unit-tested); `{$` was chosen because it never occurs in normal text. A later extension could keep macros live until save (e.g. page numbers that follow reordering) – the overlay would then store the template and expand at save time.

## Text recognition (OCR)

**No telemetry.** ONNX Runtime (from 1.2x) collects a device id and sends telemetry; on Linux it even runs `blkid` and
`hostname` through `/bin/sh`. `RapidOcrEngine` switches that off before the models load (`ORT_DISABLE_TELEMETRY` in the
native environment via `setenv` on Linux/macOS, `OrtEnv.DisableTelemetryEvents()`), the container image sets the
variable too. Without it OCR crashed the server in the shell-less container image.
Makes scanned PDFs (pages without text) searchable: render the page, recognise the text and lay it over the picture as an **invisible text layer**. The page looks the same; searching, selecting and copying then work in every PDF viewer.

**Constraints (decided):**
- **License:** engine, models and all dependencies open source and compatible with the app's MIT license (Apache-2.0, MIT, BSD, BSL; **no GPL**).
- **Platforms:** like the app. ONNX Runtime ≥ 1.24 has no Intel macOS builds and needs macOS 14, so the whole app supports macOS only on Apple Silicon from macOS 14 (no `osx-x64`, `LSMinimumSystemVersion` 14.0).
- **Languages:** German and English. **Models ship with the app** (offline, no download, no network code).

**Engine: RapidOcrNet** (PaddleOCR PP-OCRv5 Latin models on ONNX Runtime, Apache-2.0, natives for all three platforms via NuGet like pdfium). Compared with Tesseract 5 on 5 pages × 6 variants (300/150 dpi, fax, skewed, upside down, JPEG q30):

| Mean over 30 pages | Rapid 1024 px | **Rapid 2048 px** | Tesseract fast | Tesseract best |
|---|---|---|---|---|
| Word F1 | 84 % | **97 %** | 80 % | 74 % |
| Words with umlauts/ß | 96 % | **100 %** | 97 % | 96 % |
| Time per page | 1.2 s | 1.9 s | 1.9 s | 4.6 s |

- Tesseract fails on dot leaders (tables of contents) and mixed layouts and has no maintained .NET wrapper with natives for all platforms. On two real German scans (19 pages, ~7 600 words) both were usable: Rapid ~0.4 % word errors (better with umlauts), Tesseract ~0.6 %. Rapid reads upside-down lines through its classifier.
- Settings: `ImgResize = 2048` (with the default 1024 small print gets lost), `ReturnWordBox = true`, ONNX memory arena off (`EnableCpuMemArena`/`EnableMemoryPattern = false`: peak 0.84 instead of 1.5 GB, 0.33 instead of 1.27 GB kept afterwards, ~5 % slower).
- 2–5 s per A4 page, so progress and Cancel are a must.

**Core (`src/EasyPDF.Core/Ocr/`):**
- `IOcrEngine.Recognize(RenderedPage, token)` → `OcrWord(Text, Box, Confidence)` in picture pixels. `RapidOcrEngine` hands the BGRA buffer to RapidOcrNet as `SKBitmap`; models in `ocr-models/` next to the program with absolute paths (RapidOcrNet looks relative to the current directory by default; the Core project copies them itself, the package's build step is off with `ExcludeAssets="build"`); calls run one at a time (lock).
- `PageOcr` renders the page on the `PdfiumWorker` at 300 dpi (longer side at most 7000 px) as it is shown, i.e. with the extra rotation, and recognises it **outside** the pdfium thread (`Task.Run`), so reading stays smooth. Words are mapped back into the overlay page space (`Rotation = 360 − extra rotation`, like `TextOverlay`); `PdfSaver` converts to PDF user space via `FPDF_DeviceToPage`, which covers /Rotate and CropBox.
- `PdfSource.CountTextCharsAsync` = 0 → the page needs OCR; pages with text are skipped by default. `PdfSource.ReadWordsAsync` reads an existing text layer in the same space (truth in the tests, base for copying and selecting text).

**Model and saving:** `PageEntry.OcrWords`; `EditableDocument.SetOcrWords` (per `PageEntry.Id`, since OCR runs in the background) is one undo step; the words travel with the page (move, Extract, Split, Concatenate). `PdfSaver.ApplyOcr`: one invisible Helvetica text object per word (render mode INVISIBLE), font size 0.6 × box height (boxes are about line height), baseline 0.3 font sizes below the box centre, stretched to the box width, a space appended to every word (otherwise copying gives "Rechnungüber"); then `FPDFPage_GenerateContent`, which only adds a new stream and leaves the existing content alone (`OverlayContentTests`). WinAnsi covers ä ö ü ß €, other characters become "?".

**App:** `RecognizeTextCommand` (Edit menu, ribbon group "Recognize" on Home and Organize, page context menu) → `OcrDialog` (pages as in the Rotate dialog, "Skip pages that already contain text" on by default, [X] = Cancel). The engine is loaded on first use and kept for the session. Status bar shows page x of y, progress and Cancel (also Esc); cancelling discards everything; afterwards and until saved the status bar mentions the pending text layer. The recognised text can be copied (Organize ▸ Copy text) and selected with the select-text tool.

**Packaging and licenses:** `licenses/` (with `THIRD-PARTY-NOTICES.txt` incl. the RapidOcrNet NOTICE, Apache-2.0 §4d) is copied next to the program and thus into every installer; a test checks that every referenced file exists. About shows the OCR versions and a "Third-party licenses" button. Publish leaves out native `lib*.pdb` and `*.lib` (win-x64 268 → 168 MB). The `.deb` needs `libc6 (>= 2.28)` and `libstdc++6 (>= 6)` for `libonnxruntime.so`. RapidOcrNet pulls `SkiaSharp.NativeAssets.Linux.NoDependencies`; the app excludes it so Avalonia's SkiaSharp natives are used. CI runs the OCR tests on Linux and macOS before packaging.
