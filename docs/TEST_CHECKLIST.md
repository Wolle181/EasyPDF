# Manual test checklist

Automated tests (`dotnet test EasyPDF.slnx`) cover logic and UI behaviour headless on Windows. This list is for what only a
real desktop can show — mainly **macOS** and **Linux**, which were not testable during development.

`<App>` = the shown name of the brand under test (`<app>` in lower case; [branding/README.md](../branding/README.md)).

Test file: `demo-pdfs/Annual Report 2025.pdf` (19 pages, made by `tools/demo-pdf/make_demo_pdf.py`). For Reduce size use a PDF with scanned pages or photos. For bookmarks use any PDF with a table of contents.
Start: `dotnet run --project src/EasyPDF.App -- <file.pdf>` or the published build from `artifacts/publish/<rid>/`.

## All platforms
- [ ] App starts, file opens (dialog, drag & drop onto the window, command line, Recent files)
- [ ] Reading mode: mouse wheel scrolls horizontally, Shift+wheel vertically, arrow keys / PageUp / PageDown / Home / End work
- [ ] Zoom: Ctrl/Cmd+wheel zooms around the pointer, Ctrl/Cmd +/−/0, zoom box in the View tab, Fit height/width/page
- [ ] Organize mode (Ctrl/Cmd+2): select (click, Ctrl/Cmd+click, Shift+click, Ctrl/Cmd+A), drag one page and a multi-selection, auto-scroll at the edge, double-click opens the page in Reading mode
- [ ] Right-click a page in both modes: Insert before/after (file dialog), Rotate, Extract, Delete, mode switch
- [ ] Rotate dialog: all directions × all/current/selected/range; invalid range disables OK; one undo step
- [ ] Merge (with and without the current document), Extract, Split
- [ ] Save As Image (Organize ▸ Output) at 150/200/300 dpi, colour and grey: the pages look as before (also stamps, added text, comments, a rotated page), no text can be selected or found, the original file is unchanged
- [ ] Reduce File Size with a scanned PDF: all three levels, the message shows old and new size, the smaller file looks the same (text, coloured boxes, pictures in place), bookmarks are kept, the original file is unchanged
- [ ] Save, Save As, overwrite of the open file; reopen shows correct order/rotation; bookmarks still present
- [ ] Unsaved-changes prompt on open/close/exit; closing the prompt with [X] cancels
- [ ] Settings: theme System/Light/Dark and language System/English/Deutsch switch immediately (menu, ribbon, dialogs, status bar) and survive a restart; also in the installed/portable program (Native AOT: the German texts are compiled into the program, there is no `de/` folder next to it)
- [ ] Ribbon in a narrow window: make the window narrower step by step (down to its minimum width) on every tab: groups on the right first show small buttons, then icons only (tooltips keep the label); nothing is cut off; widening brings everything back; switching the language at a narrow width still fits. Display scaling 150 %/200 %: groups may collapse to "<Group> ▾", which opens the group below it and works from there
- [ ] Windows/Linux: no menu bar on a fresh install; Settings ▸ "Show the menu bar" shows/hides it in all windows at once and survives a restart; shortcuts (Ctrl+O, Ctrl+S, F1 …) still work; all commands are in the ribbon (View ▸ Navigate/Panels/Application, Blank page before/after, Recent ▸ Clear list). macOS: the option is not shown
- [ ] Quick guide: F1, Help ▸ Quick Guide, the ? button in the ribbon and "New here?" on the start page open it; it stays open beside the document (not modal), a second F1 brings it to the front, Esc closes it; icons in light and dark theme, German and English text, macOS shows ⌘ instead of Ctrl
- [ ] House style (branch `LayoutINTER`) in light and dark theme: brand header with mark, name and subtitle (German subtitle after switching the language) and white ?/ⓘ icons, navy band with the tabs, yellow line under it, white ribbon panel; all texts in Barlow (also menus, tooltips, context menus, dialogs – no fallback font); icons navy with a yellow fill, Delete/Exit red; Organize and sidebar: white page cards, the selected one navy; dialog OK/Save buttons yellow, others white with a grey frame; nothing unreadable (white on white, navy on navy) on hover, pressed or disabled
- [ ] Ribbon without the menu bar: the arrow at the right end of the tab row folds it away (tooltip names Ctrl+F1 / ⌘+F1) and shows it again; with the ribbon folded a single click on any tab (also the selected one) opens it; a double-click on a tab of an open ribbon folds it
- [ ] Help ▸ About shows the four-part version and the commit hash, e.g. `1.0.0.97 (3fa9c1d2)`; "Copy version info" fills the clipboard

## Context menu
- [ ] Windows (after installing the MSI): right-click one PDF ▸ "Mit <App> lesen" opens it; select several PDFs ▸ "Mit <App> lesen" → one window per file (one process); several PDFs ▸ "Mit <App> zusammenführen" → one Merge PDFs dialog with all files; on Windows 11 under "Weitere Optionen anzeigen"
- [ ] Linux/KDE Dolphin: one PDF shows only "Read with…", several show "Read with…" (one window per file) and "Merge with…"; Nemo the same
- [ ] macOS Finder: several PDFs ▸ Open With ▸ <App> → Merge PDFs dialog; one PDF opens it

## Several windows
- [ ] With a document open, File ▸ Open / Recent / drag & drop opens the file in a new window (offset, same size); an empty window takes the file itself; opening a file that is already open brings its window to the front
- [ ] File ▸ New Window (Ctrl/Cmd+N); Close in one of several windows closes that window, in the last one it leaves an empty window
- [ ] Exit / Quit (macOS: Cmd+Q and Dock ▸ Quit) with unsaved changes in two windows: asked once per window, Cancel keeps that window and the rest open
- [ ] Double-click a PDF in Explorer/Finder/file manager while the app runs: no second process; an empty window is used, otherwise a new window comes to the front (Windows: not just a flashing taskbar button)
- [ ] Merge from the context menu while the app runs: one Merge PDFs dialog in a new window
- [ ] Recent files and a language change show up in all windows
- [ ] Merge with documents in other windows: "Open documents" adds all of them at once (an edited one says so in its row), a second click adds none twice, a removed one can be added again; OK combines them with their unsaved edits, also after closing the other window before OK

## Command line
- [ ] `<App> a.pdf` opens the file; `<App> a.pdf b.pdf c.pdf` opens the Merge PDFs dialog listing a, b, c in this order; reorder + OK gives the combined document; a missing file is reported
- [ ] Linux: selecting several PDFs in the file manager ▸ Open with <App> goes to the Merge PDFs dialog

## Keyboard
- [ ] Ctrl+Home / Ctrl+End (macOS: Cmd+Home/End, on laptops Fn+Cmd+←/→) jump to the first / last page in Reading and Organize mode

## Swiping
- [ ] Reading mode: drag with the left mouse button scrolls (left/right, up/down when zoomed in), a fast swipe glides on and slows down, a click stops it; a plain click still marks the page red; trackpad and touch screen behave sensibly

## Page sidebar and status bar zoom
- [ ] Reading mode: thumbnails on the left follow scrolling/zooming; a click jumps to the page; View ▸ Thumbnails / Ctrl+T hides it (remembered after restart); hidden in Organize mode
- [ ] Status bar right: zoom out/in buttons and the % menu (steps, fit modes) work in Reading mode; in Organize mode they size the thumbnails

## Header
- [ ] Brand header: the gear left of the ? opens Settings (with and without a document, on every tab, also with the ribbon folded away); ? opens the quick guide, ⓘ About
- [ ] Introduction video: ▶ in the header and Help ▸ Introduction Video play the video in the UI language (German/English) in the system's video player (Windows: Media Player, macOS: QuickTime, Linux: the default player); Linux without a video player: a message instead; installed app: `videos/` next to the program (macOS: inside the .app)

## Stamps, blank pages, text
- [ ] Blank page: "Blank page" button / context menu / Edit menu inserts a page before or after the current page with the size of its neighbour; undo removes it; saved file contains it
- [ ] Replace pages: context menu / ribbon / Edit menu → dialog; selection with a gap or range like "2, 4" is refused; replace by a file (also only some of its pages) and by the document of another window (with its unsaved changes); new pages sit where the replaced ones were; one undo step; Cancel/[X] leaves the file closed (can be deleted)
- [ ] With Windows Security ▸ "Controlled folder access" switched on and <App> not allowed: stamps and templates work from `%LocalAppData%\<App>` (path in Settings), samples present, a new template is saved; a chosen folder that can't be used shows an error with the reason when opened or saved, never nothing
- [ ] After the first start (also when `Documents/<App>/Stamps` was kept from an uninstalled version): the folder holds the sample "Stempel 'gescannt'" and the gallery shows it; deleted from the folder it does not come back after a restart
- [ ] Number stamps: the gallery shows a section "Nummern (21)" (English: "Numbers (21)"); opened, it lists 00–20 and stays open for the next pick; a number is placed about 20 mm wide and looks sharp in the printout
- [ ] Stamp: pick a picture from the gallery (PNG with transparency; a JPG in the stamp folder is not shown, and the small print in the gallery and in Settings says stamps are PNG); it follows the pointer slightly transparent, so the page shows under it, the pointer itself is hidden over a page and comes back as a cross beside the pages, also on macOS and Linux; click a page to place it; click selects (dashed frame, corner handles), drag moves, corner resizes keeping the aspect ratio, Delete removes; Esc/right click cancels placing; each step undoable
- [ ] Stamp with a date: the gallery shows "Eingegangen" and "Received" with today's date; placed on a page, the status bar names the date; a double click opens the calendar, another date changes the picture, Ctrl/Cmd+Z brings the old one back; saved, the date is on the page in other PDF viewers too
- [ ] Stamp editor: "New stamp…" and right click ▸ "Edit…" in the gallery (the menu opens over the gallery and works on your system); draw a field, move it, pull a corner; text with macros, colour, alignment, capital letters, language; a PNG with a white background and "Make white transparent" doesn't cover the page; the editor offers only PNG and refuses a JPG; the saved stamp appears in the gallery
- [ ] Save a document with stamps in a PDF viewer other than this app: position, size and transparency are right, also after rotating the page before saving and on a page with its own /Rotate
- [ ] Text: "Insert text" then click a page opens the editor (multi-line, current font/size/colour, grows with zoom); Esc / Ctrl+Enter / click elsewhere finishes; empty text places nothing; double-click edits again, emptying deletes
- [ ] Pen colour flyout: swatches, #RRGGBB entry, font and size; settings are remembered after a restart
- [ ] "Like the text on the page" (pen colour flyout): on a PDF written in Arial/Calibri/Times New Roman (e.g. from Word), new text and templates come in that font, size and weight; on a scan or an empty page in the font and size above; the saved file shows the text in the same font in another viewer (installed font embedded: file larger by the font file) and on a computer without that font too; web version: the most similar standard font in the page's size
- [ ] Placed text: click selects, drag moves, corner scales (font size follows), Delete removes; undo/redo for each step
- [ ] Save a document with text and open it in another PDF viewer: text is real text (selectable/searchable), fonts Helvetica/Times/Courier, colour, line spacing and position match the screen, also on rotated pages; Extract and Split keep the text
- [ ] Templates: the Templates button lists the templates of the templates folder; choosing one shows the text at the pointer in its real font, size and colour (the pointer itself is hidden over a page and comes back beside the pages and after Esc or right click, also on macOS and Linux); clicking a page places the text exactly there with macros replaced ({$DATE}, {$PAGE}, {$PAGES}, {$FILE}, {$USER}); "Edit templates…" adds/renames/deletes and the folder's .txt files change accordingly
- [ ] After the first start, before the Templates button was used: `Documents/<App>/Templates` exists with the sample templates in the language the program shows (German: Datum, Eingegangen, Entwurf, Seite x von y); a .txt file written with a text editor (also with umlauts) shows up the next time the flyout opens; "Open templates folder" (flyout and Settings) opens it in the file manager
- [ ] Saving with several pages open in Reading and Organize mode shows no error (such as "different thread owns it")
- [ ] Ctrl/Cmd+Z after each of the above steps behaves; the ribbon icons (blank page, stamp, text, templates, pen) look right in light and dark theme

## Black out and white out
- [ ] "Black out" / "White out" (Home ▸ Redact, Edit menu): status bar explains the tool, the button stays highlighted; the cursor is an I-beam over text and a cross elsewhere
- [ ] Drag over text: the words are covered line by line (also over several lines and pages), a double click covers one word; drag beside the text draws a box (also on a scan or picture); a plain click draws nothing; Esc / right click / the button again ends the tool
- [ ] With "Select text" and a selection, "Black out" covers exactly the selection; covered words can no longer be selected or copied (Ctrl+C, Copy text in Organize mode)
- [ ] Without a tool: click selects a box, drag moves it, a corner resizes it freely, Delete removes it (not the page); each step undoable
- [ ] Save, then open the file in another PDF viewer (e.g. a web browser): the boxes are there; the covered text cannot be found with search, selected or copied; the rest of the page looks as before and its text can still be searched and copied; deleting/moving objects in a PDF editor shows nothing under the box
- [ ] Same on a scanned page (only a picture), a page with a form field and a comment under the box, a rotated page, and after Extract/Print/Email (they use the boxes too)
- [ ] File size after blacking out a scan grows only moderately (the page becomes a JPEG at the scan's resolution)
- [ ] Save (Ctrl+S) with boxes asks before overwriting the original: "Save a copy…" (Enter) suggests `<name>_redacted.pdf` and keeps the original, "Overwrite" replaces it, Cancel and [X] save nothing; "Don't ask again" and the checkbox in Settings switch the question off (and on again), also after a restart

## Copy and paste areas
- [ ] "Copy area" (Home ▸ Redact group, Edit menu; "Paste" next to Undo/Redo): cross cursor, status text; a frame copies the area and the copy follows the pointer at once; a click places it in the original size, on the same page, another page or a rotated page (upright as seen)
- [ ] The copy shows stamps, texts and boxes that were on the area; it covers what lies under it; drag moves it, a corner resizes it (aspect kept), Delete removes it; Ctrl/Cmd+Z undoes each step
- [ ] Esc while pasting keeps the copy; Ctrl/Cmd+V / "Paste area" places it again, also in another window
- [ ] Save, then open the file in another PDF viewer: the pasted area is there; the text that was under it cannot be found, selected or copied; the original area is unchanged

## Printing
- [ ] File ▸ Print… / Ctrl+P / ribbon button: dialog lists the installed printers with the default preselected and "All pages" chosen; "Current page", "Pages A to B" and "All pages" each print exactly those pages (try a PDF printer or a real one); copies work; invalid range disables Print
- [ ] The printout shows edits (reordered/rotated pages, stamps, text) and fits portrait and landscape pages onto the paper
- [ ] Windows: print to "Microsoft Print to PDF" and a physical printer; Linux/macOS: CUPS printers appear (`lpstat -e`), job shows up in the queue; no printers → dialog says so
- [ ] Cancelling while a large range is printing stops the job
- [ ] "Properties…" next to the printer: Windows opens the driver's own dialog (modal to the print dialog); choose a different tray / two-sided / paper size, OK → hint "Custom printer settings are used", the printout uses them; cancelling the driver dialog keeps the previous settings; another printer starts with its defaults; the next print job in the same window offers the settings again
- [ ] macOS/Linux: "Properties…" lists the CUPS options of the printer (`lpoptions -p <printer> -l`), defaults preselected; changed options reach the job (`lpstat -l -o` or the printout); "Printer defaults" resets; a printer without options says so

## Send by email
- [ ] File ▸ Send by Email… / ribbon "Email": the default email program opens a new message with the PDF attached, subject = file name; cancelling the message is not reported as an error
- [ ] Unchanged document: the attachment is the file itself; after rotating/deleting a page (unsaved): the attachment has the same name and shows the change, the document stays unsaved
- [ ] Windows: classic Outlook and Thunderbird as default email app (Settings ▸ Apps ▸ Default apps ▸ MAILTO / Simple MAPI); new Outlook / no email program → error message, app keeps working; the app stays usable while the message window is open
- [ ] macOS: Mail, Outlook and Thunderbird as "Default email reader" (Mail ▸ Settings ▸ General)
- [ ] Linux: `xdg-email` with Thunderbird and Evolution/KMail; without `xdg-utils` → error message

## Attachments and electronic invoices
- [ ] ZUGFeRD/Factur-X PDF (e.g. from a supplier or the official sample set): the bar "contains an electronic invoice" appears; **Open XML** opens it in the system's XML program (Windows: browser/editor; macOS: Xcode/TextEdit; Linux: `xdg-open`), **Save XML…** suggests `<PDF name>.xml` next to the PDF
- [ ] [X] hides the bar; opening another invoice PDF shows it again; Home ▸ **Attachments…** lists all embedded files, Open is greyed out for an `.exe`
- [ ] Rotate a page and save: the invoice is still embedded (bar shows again after reopening, other PDF viewers list it)
- [ ] PDF without attachments: no bar, Attachments… disabled

## Text recognition (OCR)
- [ ] Edit ▸ Recognize Text (OCR)… / ribbon "Recognize text…" / page context menu: dialog with pages (all, current, selected, range) and "Skip pages that already contain text"; [X] and Cancel do nothing
- [ ] A real scan (e.g. a letter scanned at 300 dpi): status bar shows "Recognizing text: page x of y…" with progress, the window stays usable (scrolling, zoom); afterwards "Text recognized on n pages", status "Unsaved changes (text layer on n pages)"; Ctrl/Cmd+Z removes it again
- [ ] After saving, open the file in other viewers (e.g. a web browser and the system's own PDF viewer): the page looks unchanged; searching finds words including umlauts (ä ö ü ß); the highlight sits on the printed word; copy and paste gives the words separated by spaces
- [ ] A scan turned sideways: rotate the page upright first, then recognize; in other viewers the text is found and selectable in reading direction
- [ ] Organize mode, select pages, Ctrl/Cmd+C (or Edit ▸ Copy Text, context menu, ribbon "Copy text"): the status bar says "Text of n page(s) copied…" for a few seconds; pasting into a text editor gives the lines of the pages in order, pages separated by an empty line (works before and after saving); pages without text say "contain no text"
- [ ] Home ▸ "Select text" (or Edit ▸ Select Text): over text the cursor is an I-beam, elsewhere the arrow; dragging over a page highlights the words in reading order (also on a page recognized just now, before saving), also on to the next pages; at the edge of the window the pages scroll on by themselves; double click selects a word; Ctrl/Cmd+A selects the whole text of the page; Ctrl/Cmd+C copies it ("Selected text copied…"); a page without text says "contains no text"; Esc removes the selection, a second Esc ends the tool; a drag without the tool moves the pages as before
- [ ] Ctrl/Cmd+C, Delete and Ctrl/Cmd+Z in a text box (zoom box, page number box) act on the box only, not on the pages
- [ ] Cancel (button or Esc) during a long scan stops after the current page, nothing changes
- [ ] Running it again with "skip" ticked reports that all pages already contain text
- [ ] Help ▸ About: row "Text recognition" with versions; "Third-party licenses" opens the licenses folder next to the program
- [ ] macOS (Apple Silicon, macOS 14+) and Linux: recognition works in the installed app (ONNX Runtime is found; the CI runs the OCR tests on both)

## Web version (real browsers: Chrome/Edge, Firefox, Safari)
- [ ] First visit (empty cache, throttled network in the developer tools): the splash shows a spinner, "Loading … n %" with a bar in the language of the settings or the browser, then "Starting …", then the app; dropping a PDF from the desktop opens it (progress for big files); a ~50 MB file passes the reverse proxy
- [ ] Organize: drag pages, rotate, delete, undo/redo; Save as… downloads (Chrome/Edge: save dialog of the system) and the file opens in the desktop app as edited
- [ ] Print: a new tab shows the PDF; the browser's print dialog prints it (a popup blocker shows a hint instead)
- [ ] Share (button "Share" with the three-dot icon; Windows Chrome/Edge, macOS Safari/Chrome, phone): the share sheet opens, the email program shows a new message with the PDF attached under the document's name; a big file first shows "document is ready", OK opens the share sheet; on Linux and in Firefox a message says the browser cannot share files and Save as follows
- [ ] Stamps: gallery with samples, "Shared" sections from `/volume1/docker/easypdf-web/shared/stamps`; place one, double-click → date dialog; New stamp… → editor, OK → in the gallery; right-click → Delete asks first
- [ ] Stamps and templates survive reloading the page and closing the browser; Export… downloads a ZIP; in another browser Import… brings them back; a private window starts with the samples only
- [ ] Templates: shared ones from `…/shared/templates` appear; an edited shared template is kept as the user's own
- [ ] E-invoice PDF: "Open XML" in the bar (and Open in Attachments) shows the XML indented as text in a new tab; other attachments only Save
- [ ] Placed text: the preview looks like the saved text (same width, Helvetica/Times/Courier)
- [ ] Caching: reload the page with the developer tools open (network, not "Disable cache"): the `_framework/*.<hash>.wasm` files come from the cache ("cached"/"disk cache"), `index.html` and `dotnet.js` answer 304; after deploying a new image the app shows the new version on the next reload
- [ ] Merge with other tabs: open two tabs of the app, a PDF in each, edit one (rotate/delete a page, unsaved); in the other tab Merge PDFs shows "Open documents", a click adds the edited one ("Open in another tab (with its unsaved changes)") and a second click adds nothing; OK gives the combined document with the edits; closing the other tab before the click gives no row; with only one tab the button is hidden
- [ ] Replace pages with other tabs: with a PDF in another tab the dialog lists it (preselected) after a moment; OK puts its pages, with that tab's unsaved edits, in place of the chosen ones; with only one tab only "File" is offered
- [ ] Two browsers (or a private window) at the same time see only their own files; after 30 minutes idle a file is gone (the app reports it)
- [ ] Settings: theme and language switch live and are kept after reloading
- [ ] Downloads: with files in `/volume1/docker/easypdf-web/downloads` the header shows the download arrow; the dialog lists one package per kind (newest), marks those of the own system, Download saves the file without leaving the page; without files the button is hidden
- [ ] About: "Deployed" shows the time of the last container build, badge "Containerized" ("Native" with `dotnet run`)

## Installers
- [ ] Two brands side by side (e.g. `installeruild-windows.cmd` and `installeruild-windows.cmd -Brand <id>`): both MSIs install next to each other, each with its own name, icon, colours, start menu entry, context menu and settings folder; both apps run at the same time, and "Read with" of one brand opens in that brand's window
- [ ] Windows: MSI installs per user to `%LocalAppData%\Programs\<App>` (shown name) **without a UAC prompt**, Start menu entry with icon, app starts; uninstall via Settings ▸ Apps removes it (again without a UAC prompt); a newer MSI replaces the old version; "Installed apps" shows the four-part version (1.0.0.97)
- [ ] Windows: uninstall via Settings ▸ Apps: after the system's own question a second one lists the folders that go too (`%AppData%\<App>`, `Documents\<App>`, in the installer's language). Cancel: nothing changes, the program still works. OK: program and folders are gone. An update never asks and keeps the folders; `msiexec /x … /qn` asks nothing and keeps them; a stamp folder chosen in the settings is never removed
- [ ] Windows: the Explorer context menu ("Read/Merge with <App>") appears for the installing user only
- [ ] Default program (Windows): MSI dialog "Preferences" shows "Use <App> as the default program for PDF files" (ticked); after Finish the Windows settings open on the <App> page, "Set default" makes it open PDFs; <App> is listed under "Open with" either way; the box is hidden when <App> is the default already; Settings ▸ "Open PDF files with <App>" opens the same page and afterwards says "<App> is the default program …"; uninstall removes the registration
- [ ] Shown name (`AppDisplayName`, <App>): Windows start menu entry, "Installed apps" and "Open with" say <App>; Linux application menu and Dolphin/Nemo menus; macOS: `<App>.app` in the disk image, menu bar and Dock say <App>
- [ ] Windows MSI: dialog "Preferences" after the install folder (Back/Next work, also Back from "Ready to install"); the chosen language and appearance are active at the first start, also for a user who already had other settings; changing them in the app's settings sticks after a restart; reinstalling preselects the previous choice; Repair does not reset the app's settings
- [ ] Linux: `sudo apt install ./<App>-<version>-Linux-x64.deb` → app in `/opt/<App>`, in the application menu with icon, `<app> file.pdf` opens the file; `sudo apt remove <app>`
- [ ] Default program (Linux, GNOME/KDE/Cinnamon): first start of the installed .deb asks "Should <App> open PDF files from now on?" once; Yes → double-click on a PDF opens <App> (`xdg-mime query default application/pdf` = <app>.desktop); No → not asked again; Settings button does the same; from the unpacked .tar.gz no question, the button says "Only possible for the installed program"
- [ ] Default program (macOS): first start of <App>.app (from /Applications) asks once; Yes → double-click on a PDF in Finder opens <App> (Finder ▸ Get Info ▸ Open with shows it); Settings button does the same; started with `dotnet run` no question
- [ ] User folders have the shown name: settings `%AppData%\<App>` (Linux `~/.config/<App>`, macOS `~/Library/Application Support/<App>`), stamps `Documents/<App>/Stamps`, templates `Documents/<App>/Templates` (`%LocalAppData%\<App>` if Documents can't be written); "Installed apps" shows the publisher Wolfgang Ahting
- [ ] Linux: a package of a later commit replaces the installed one (`apt install ./…deb`); the same commit counts as already installed (`--reinstall` installs it anyway)
- [ ] macOS: Finder ▸ Get Info on the app shows "Version 1.0.0 (97)"
- [ ] macOS: open the .dmg, drag the app to Applications, first start via right-click ▸ Open; Dock icon is the app icon; macOS 13 refuses to start it (minimum macOS 14), Intel Macs are not supported
- [ ] All installers contain the folders `ocr-models` and `licenses` next to the program

## macOS
- [ ] Menu bar is at the top of the screen (not inside the window); shortcuts use Cmd
- [ ] About and Settings reachable (app menu / Help menu), Cmd+, opens Settings
- [ ] Trackpad: two-finger scroll moves horizontally/vertically, pinch zooms around the cursor
- [ ] Gatekeeper: the unsigned build needs right-click ▸ Open (signing/notarization is a later task)

## Linux
- [ ] Menu bar is shown inside the window; native file dialogs work (portal/GTK)
- [ ] Fonts and text rendering look right; HiDPI scaling is respected
- [ ] `libpdfium.so` is found next to the executable (published build)
