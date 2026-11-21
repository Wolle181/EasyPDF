// Records the demo video (no sound, banners at the bottom) from the real app, headless, in one language per run:
//   dotnet run -c Release --project tools/DemoVideo -- [--language de|en] [--web] [--ffmpeg <file>] [--out <file.mp4>]
// --web records the web version (its commands, dialogs as overlays, a browser bar above it, upload, share, desktop downloads).
// ffmpeg must be on the PATH or given with --ffmpeg; make-video.cmd downloads it for the one run and records both languages (Windows).
// The app is the brand the build selects (-p:Brand=<id> or EASYPDF_BRAND; branding/<id>/make-presentation-videos.cmd does it).
// Default output: branding/<brand>/videos/introduction.<de|en>.mp4 (web: branding/<brand>/videos/web/…), committed with the
// brand: the build copies them next to the program (videos/) or into the web page (wwwroot/videos/), the button in the header plays them.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using EasyPDF.App;
using EasyPDF.App.Controls;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Templates;
using EasyPDF.DemoVideo;

[assembly: AvaloniaTestApplication(typeof(DemoAppBuilder))]

namespace EasyPDF.DemoVideo;

public class DemoAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal static class Program
{
    /// <summary>The brand profile the build uses (Brand in Directory.Build.props), passed in as assembly metadata.</summary>
    public static string Brand { get; } = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "Brand").Value!;

    public static int Main(string[] args)
    {
        var root = RepoRoot();
        var ffmpeg = Option(args, "--ffmpeg") ?? "ffmpeg";
        var language = Option(args, "--language") ?? "de";
        if (language is not ("de" or "en"))
            throw new ArgumentException($"Unknown language: {language}");
        var web = args.Contains("--web");
        var output = Path.GetFullPath(Option(args, "--out")
            ?? Path.Combine(root, "branding", Brand, "videos", web ? "web" : "", $"introduction.{language}.mp4"));
        // Dates in the picture follow the language of the video, not the machine that records it
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(language);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        using var session = HeadlessUnitTestSession.StartNew(typeof(DemoAppBuilder));
        session.Dispatch<int>(async () =>
        {
            await new Script(root, ffmpeg, output, language, web).RunAsync();
            return 0;
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(output);
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "EasyPDF.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>The storyboard: one method per feature, each with its banner.</summary>
internal sealed class Script(string root, string ffmpeg, string output, string language, bool web)
{
    private string L(string german, string english) => language == "de" ? german : english;

    // In the picture (merge and split dialogs show the paths), so a tidy folder; only if it does not exist yet, as it is deleted at the end
    private readonly string _work = CreateWorkFolder(language == "de" ? "Beispiele" : "Samples");
    private Recorder _rec = null!;
    private AppSettings _settings = null!;
    private MainWindow _window = null!;
    private readonly List<MainWindow> _windows = [];
    private string _report = "", _invoice = "", _cover = "", _annex = "";

    /// <summary>
    /// Documents\&lt;name&gt; if it is free and may be created, else a folder in temp. Controlled folder access (Windows
    /// Security) answers a blocked program with "file not found" instead of "access denied".
    /// </summary>
    private static string CreateWorkFolder(string name)
    {
        var tidy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name);
        if (!Directory.Exists(tidy))
        {
            try
            {
                Directory.CreateDirectory(tidy);
                return tidy;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Cannot create {tidy} ({e.Message}), so the video shows a temp folder instead. "
                    + $"If controlled folder access is on, allow {Environment.ProcessPath} in Windows Security.");
            }
        }
        var temp = Path.Combine(Path.GetTempPath(), "easypdf-demovideo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        return temp;
    }

    public async Task RunAsync()
    {
        try
        {
            Prepare();
            using (_rec = new Recorder(ffmpeg, output, Path.Combine(root, "src", "EasyPDF.App", "Assets", "Fonts"),
                       Path.Combine(root, "branding", Program.Brand, "icons", "app-128.png"))
                   { BrowserAddress = web ? WebAddress() : null })
            {
                if (web)
                {
                    await WebStoryAsync();
                    return;
                }
                await OpenAsync(_report);
                await _rec.Card(_rec.TitleCard(EasyPDF.Core.AppInfo.Name, L("PDFs lesen, ordnen und bearbeiten", "Read, organize and edit PDFs"), L("lokal und offline", "local and offline")), 3, fadeIn: true, fadeOut: true);
                await ReadingAsync();
                await OrganizeAsync();
                await SplitAsync();
                await MergeAsync();
                await OcrAsync();
                await StampAsync();
                await DynamicStampAsync();
                await TextAsync();
                await TemplatesAsync();
                await RedactAsync();
                await ThemeAsync();
                await _rec.Card(_rec.TitleCard(EasyPDF.Core.AppInfo.Name, "Windows · macOS · Linux", L("Deutsch und Englisch · Hell und Dunkel", "English and German · Light and dark")), 3.5, fadeIn: true, fadeOut: false);
            }
        }
        finally
        {
            await CleanUpAsync();
        }
    }

    /// <summary>The open documents hold their files; only when they are closed can the sample folder go.</summary>
    private async Task CleanUpAsync()
    {
        foreach (var window in _windows)
            if (window.ViewModel.Document is { } document)
                await document.DisposeAsync();
        try
        {
            Directory.Delete(_work, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not delete {_work}: {e.Message}");
        }
    }

    private void Prepare()
    {
        var documents = new DemoDocuments(Path.Combine(root, "src", "EasyPDF.App", "Assets", "Fonts"), language);
        _report = documents.Report(Path.Combine(_work, L("Jahresbericht 2026.pdf", "Annual Report 2026.pdf")));
        _invoice = documents.ScannedInvoice(Path.Combine(_work, L("Rechnung (Scan).pdf", "Invoice (scan).pdf")));
        _cover = documents.Simple(Path.Combine(_work, L("Anschreiben.pdf", "Cover Letter.pdf")), L("Anschreiben", "Cover Letter"), 2, 3);
        _annex = documents.Simple(Path.Combine(_work, L("Anlagen.pdf", "Attachments.pdf")), L("Anlagen", "Attachments"), 2, 1);

        var stamps = Path.Combine(_work, L("Stempel", "Stamps"));
        Directory.CreateDirectory(stamps);
        var samples = Path.Combine(root, "src", "EasyPDF.Core", "Stamps", "Samples");
        foreach (var name in new[] { L("Eingegangen.png", "Received.png"), L("Stempel 'gescannt'.png", "Received.png"), Path.Combine("Numbers", "01.png"), Path.Combine("Numbers", "02.png") }.Distinct())
            File.Copy(Path.Combine(samples, name), Path.Combine(stamps, Path.GetFileName(name)));

        if (web)
        {
            PlatformFeatures.Current = WebCommands.Features;
            var version = typeof(Program).Assembly.GetName().Version!.ToString();
            DesktopDownloads.Web = new DesktopDownloads.WebPackages(
                [.. Enum.GetValues<EasyPDF.Core.Downloads.DesktopPackageKind>().Select(kind => new EasyPDF.Core.Downloads.DesktopPackage(
                    EasyPDF.Core.Downloads.DesktopPackage.FileName(EasyPDF.Core.AppInfo.Name, version, kind), 60_000_000, kind, version))],
                DesktopDownloads.KindsFor("Windows"), _ => { });
            IntroVideo.Web = new IntroVideo.WebVideos(["de", "en"], _ => Task.CompletedTask);
        }
        _settings = new AppSettings { Language = language, StampFolder = stamps, TemplateFolder = Path.Combine(_work, L("Vorlagen", "Templates")), TextColor = "#C00000", TextFontSize = 14 };
        AppEnvironment.Apply(_settings);
        TemplateLibrary.ProvideSamples(_settings.TemplateFolder, SampleContent.Templates(), _settings.ProvidedSamples);

        // The headless platform opens popups as separate surfaces; in the overlay layer they are part of the window's frame
        Application.Current!.Styles.Add(new Style(x => x.OfType<Popup>())
        {
            Setters = { new Setter(Popup.ShouldUseOverlayLayerProperty, true) },
        });
    }

    #region Helpers

    private HorizontalPageView PageView => _window.FindControl<HorizontalPageView>("PageView")!;
    private ThumbnailGrid OrganizeGrid => _window.FindControl<ThumbnailGrid>("OrganizeView")!;
    private ThumbnailGrid Sidebar => _window.FindControl<ThumbnailGrid>("SidebarView")!;

    /// <summary>A cut: a fresh window with the document, loaded before it comes into the picture.</summary>
    private async Task OpenAsync(string? path)
    {
        var old = _rec.Window;
        var window = new MainWindow(_settings) { Width = Recorder.Width / Recorder.Scale, Height = (Recorder.AppHeight - _rec.AppTop) / Recorder.Scale };
        _windows.Add(window);
        window.Show();
        window.SetRenderScaling(Recorder.Scale);
        if (path != null)
            await window.OpenPathAsync(path);
        await Recorder.Settle(1500);
        window.FindControl<HorizontalPageView>("PageView")!.Clock = () => _rec.VideoMs;
        _window = window;
        _rec.Window = window;
        // A click on a page marks it with a red frame until the next one; in the picture it would only look like an error
        _rec.EachFrame = () => window.ViewModel.MarkedPageId = -1;
        old?.Hide();
    }

    /// <summary>The visible ribbon or status bar button of a command.</summary>
    private RibbonButton ButtonOf(ICommand command) =>
        _window.GetVisualDescendants().OfType<RibbonButton>().First(b => ReferenceEquals(b.Command, command) && b.IsEffectivelyVisible);

    private Control Tab(int index) =>
        _window.FindControl<Ribbon>("MainRibbon")!.GetVisualDescendants().OfType<TabItem>().ElementAt(index);

    /// <summary>A point of a page in the reading view, given in page points as shown.</summary>
    private Point OnPage(int page, double x, double y)
    {
        var view = PageView;
        var rect = view.PageRectInViewport(page);
        var scale = rect.Width / _window.ViewModel.Document!.Pages[page].DisplaySize.Width;
        return view.TranslatePoint(new Point(rect.X + x * scale, rect.Y + y * scale), _window)!.Value;
    }

    /// <summary>The middle of a page in a thumbnail grid.</summary>
    private Point InGrid(ThumbnailGrid grid, int page, double fractionX = 0.5)
    {
        double left = double.MaxValue, right = 0, top = double.MaxValue, bottom = 0;
        for (double y = 0; y < grid.Bounds.Height; y += 4)
            for (double x = 0; x < grid.Bounds.Width; x += 4)
                if (grid.PageIndexAt(new Point(x, y)) == page)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
        if (right == 0)
            throw new InvalidOperationException($"Page {page} is not in view.");
        return grid.TranslatePoint(new Point(left + (right - left) * fractionX, (top + bottom) / 2), _window)!.Value;
    }

    /// <summary>The button with this text in an open flyout or in the dialog on top.</summary>
    private Button ButtonNamed(string text) =>
        DialogRoot.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == text && b.IsEffectivelyVisible);

    /// <summary>A point of the stamp editor's picture, as shares of its size.</summary>
    private static Point OnStamp(StampEditorDialog dialog, double x, double y)
    {
        var area = dialog.Canvas.PictureArea;
        return dialog.Canvas.TranslatePoint(new Point(area.X + x * area.Width, area.Y + y * area.Height), dialog)!.Value;
    }

    /// <summary>The web version's dialogs: overlays in the page.</summary>
    private DialogLayer Layer => _window.FindControl<DialogLayer>("Dialogs")!;

    /// <summary>Where the open dialog's controls are: its window (desktop) or the overlay layer (web).</summary>
    private Visual DialogRoot => web && Layer.HasOpenDialog ? Layer : _rec.TopWindow;

    private Button DialogOk() => DialogRoot.GetVisualDescendants().OfType<Button>().First(b => b.IsDefault && b.IsEffectivelyVisible);

    private Button DialogCancel() => DialogRoot.GetVisualDescendants().OfType<Button>().First(b => b.IsCancel && b.IsEffectivelyVisible);

    private Task DialogOpen() => _rec.WaitUntil(() => web ? Layer.HasOpenDialog : _window.OwnedWindows.Any(w => w.IsVisible));

    private Task DialogClosed() => _rec.WaitUntil(() => web ? !Layer.HasOpenDialog : !_window.OwnedWindows.Any(w => w.IsVisible));

    #endregion

    #region Web version

    /// <summary>The address in the browser bar: the brand's web address, without the scheme.</summary>
    private static string WebAddress() =>
        VersionInfo.WebAppUrl.Length > 0 ? new Uri(VersionInfo.WebAppUrl).Host : EasyPDF.Core.AppInfo.Name.ToLowerInvariant() + ".example";

    /// <summary>The storyboard of the web version: what it offers, with upload, share and the desktop version instead of local files.</summary>
    private async Task WebStoryAsync()
    {
        await OpenAsync(null);
        await _rec.Card(_rec.TitleCard(EasyPDF.Core.AppInfo.Name, L("PDFs lesen, ordnen und bearbeiten", "Read, organize and edit PDFs"), L("im Browser, ohne Installation", "in the browser, nothing to install")), 3, fadeIn: true, fadeOut: true);
        await UploadAsync();
        await ReadingAsync();
        await OrganizeAsync();
        await WebMergeAsync();
        await OcrAsync();
        await StampAsync();
        await TextAsync();
        await RedactAsync();
        await ShareAsync();
        await DesktopVersionAsync();
        await ThemeAsync();
        await _rec.Card(_rec.TitleCard(EasyPDF.Core.AppInfo.Name, "Chrome · Edge · Firefox · Safari", L("Deutsch und Englisch · Hell und Dunkel", "English and German · Light and dark")), 3.5, fadeIn: true, fadeOut: false);
    }

    /// <summary>Files dragged in from outside the page: the pointer comes from the left edge and lets them go at <paramref name="target"/>.</summary>
    private async Task DropAsync(Point target, params string[] paths)
    {
        await _rec.MoveTo(new Point(4, target.Y + 60), 0.25);
        await _rec.MoveTo(target, 1.1);
        var data = new DataTransfer();
        foreach (var path in paths)
            data.Add(DataTransferItem.CreateFile((await _window.StorageProvider.TryGetFileFromPathAsync(new Uri(path)))!));
        foreach (var type in new[] { Avalonia.Input.Raw.RawDragEventType.DragEnter, Avalonia.Input.Raw.RawDragEventType.DragOver, Avalonia.Input.Raw.RawDragEventType.Drop })
            _window.DragDrop(target, type, data, DragDropEffects.Copy, RawInputModifiers.None);
    }

    private async Task UploadAsync()
    {
        _rec.Banner(L("PDF hineinziehen", "Drag a PDF in"), L("Oder „Öffnen“ wählen. Die Datei bleibt nur für Ihre Sitzung auf dem Server.", "Or choose “Open”. The file stays on the server for your session only."));
        await _rec.Pause(1.2);
        var view = _window.FindControl<MainView>("View") ?? _window.GetVisualDescendants().OfType<MainView>().First();
        await DropAsync(view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height * 0.6), _window)!.Value, _report);
        await _rec.WaitUntil(() => _window.ViewModel.Document != null, 30, maxVideoSeconds: 1);
        await Recorder.Settle(800);
        PageView.Clock = () => _rec.VideoMs;
        await _rec.Pause(1.5);
    }

    private async Task WebMergeAsync()
    {
        var vm = _window.ViewModel;
        _rec.Banner(L("Dateien zusammenführen", "Merge files"), L("Weitere PDFs in den Dialog ziehen und die Reihenfolge festlegen.", "Drag more PDFs into the dialog and set the order."));
        await _rec.Click(Tab(0));
        await _rec.Pause(0.3);
        await _rec.Click(ButtonOf(vm.ConcatenateCommand));
        await DialogOpen();
        await _rec.Pause(0.8);
        var list = Layer.GetVisualDescendants().OfType<ListBox>().First();
        await DropAsync(list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height * 0.7), _window)!.Value, _cover, _annex);
        await _rec.WaitUntil(() => list.ItemCount >= 3, 30, maxVideoSeconds: 1);
        await _rec.Pause(1);
        var items = Layer.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        await _rec.Click(items[1]);
        await _rec.Pause(0.4);
        await _rec.Click(Layer.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == Loc.T("ConcatUp")));
        await _rec.Pause(1);
        await _rec.Click(DialogOk());
        await DialogClosed();
        await _rec.WaitUntil(() => !vm.IsBusy, 30, maxVideoSeconds: 1);
        await Recorder.Settle(800);
        await _rec.Click(ButtonOf(vm.ShowOrganizeCommand));
        await _rec.Pause(2.4);
    }

    private async Task ShareAsync()
    {
        var vm = _window.ViewModel;
        _rec.Banner(L("Herunterladen oder teilen", "Download or share"), L("Speichern lädt die PDF herunter, Teilen gibt sie an E-Mail und Messenger.", "Save downloads the PDF, Share hands it to email and messengers."));
        await _rec.Click(Tab(0));
        await _rec.MoveTo(Recorder.PointIn(ButtonOf(vm.SaveAsCommand)), 0.8);
        await _rec.Pause(1.4);
        await _rec.MoveTo(Recorder.PointIn(ButtonOf(vm.SendByMailCommand)), 0.7);
        await _rec.Pause(2);
    }

    private async Task DesktopVersionAsync()
    {
        _rec.Banner(L("Auch für Ihren Computer", "Also for your computer"), L("Die Desktop-Version für Windows, macOS und Linux arbeitet ganz offline.", "The desktop version for Windows, macOS and Linux works fully offline."));
        await _rec.Click(_window.FindControl<RibbonButton>("DownloadButton")!);
        await DialogOpen();
        await _rec.Pause(2.2);
        var mac = Layer.GetVisualDescendants().OfType<RadioButton>().First(r => r.Name == DownloadsView.SystemButtonName(DesktopSystem.MacOS));
        await _rec.Click(mac);
        await _rec.Pause(1.2);
        var linux = Layer.GetVisualDescendants().OfType<RadioButton>().First(r => r.Name == DownloadsView.SystemButtonName(DesktopSystem.Linux));
        await _rec.Click(linux);
        await _rec.Pause(1.6);
        await _rec.Click(DialogCancel());
        await DialogClosed();
        await _rec.Pause(0.4);
    }

    #endregion

    private async Task ReadingAsync()
    {
        _rec.Banner(L("Lesen wie im Buch", "Read like a book"), L("Die Seiten liegen nebeneinander: mit der Maus wischen, ein schneller Wisch gleitet weiter.", "Pages sit side by side: swipe with the mouse, and a fast swipe glides on."));
        await _rec.Pause(1.2);
        var y = PageView.Bounds.Height / 2;
        var start = PageView.TranslatePoint(new Point(PageView.Bounds.Width * 0.8, y), _window)!.Value;
        var end = start - new Point(PageView.Bounds.Width * 0.35, 0);
        await _rec.Drag(start, end, seconds: 0.35, fling: true);
        await _rec.Pause(2.2);
        await _rec.Drag(start, end + new Point(150, 0), seconds: 0.9);
        await _rec.Pause(0.9);

        _rec.Banner(L("Stufenlos zoomen", "Zoom freely"), L("Strg und Mausrad zoomen genau dort, wo der Zeiger steht.", "Ctrl and the mouse wheel zoom exactly where the pointer is."));
        await _rec.MoveTo(PageView.TranslatePoint(new Point(PageView.Bounds.Width * 0.5, y * 0.8), _window)!.Value, 0.5);
        await _rec.Pause(0.4);
        await _rec.Wheel(1, 6);
        await _rec.Pause(1);
        await _rec.Wheel(-1, 6, 0.14);
        await _rec.Pause(0.5);

        _rec.Banner(L("Seitenleiste", "Page sidebar"), L("Die Miniaturen folgen dem Lesen. Ein Klick springt zur Seite.", "The thumbnails follow your reading. One click jumps to the page."));
        await _rec.Pause(0.7);
        await _rec.Click(InGrid(Sidebar, Math.Max(0, Sidebar.ActivePage - 1))); // the page before the one being read: it is in view
        await _rec.Pause(1.8);
    }

    private async Task OrganizeAsync()
    {
        var vm = _window.ViewModel;
        _rec.Banner(L("Seiten ordnen", "Organize pages"), L("Die Übersicht zeigt alle Seiten. Per Drag & Drop kommen sie an ihren Platz.", "The overview shows every page. Drag & drop puts them in place."));
        await _rec.Click(ButtonOf(vm.ShowOrganizeCommand));
        await _rec.Pause(1.5);
        await _rec.Drag(InGrid(OrganizeGrid, 7), InGrid(OrganizeGrid, 2, 0.05), seconds: 1.3);
        await _rec.Pause(1.8);

        _rec.Banner(L("Rechte Maustaste", "Right mouse button"), L("Drehen, löschen, einfügen, kopieren und mehr: per Rechtsklick auf eine Seite.", "Rotate, delete, insert, copy and more: right-click a page."));
        await _rec.Pause(0.4);
        await ContextMenuAsync(OrganizeGrid, InGrid(OrganizeGrid, 4), Loc.T("BtnRotateRight"));
        await _rec.Pause(1.1);
        await ContextMenuAsync(OrganizeGrid, InGrid(OrganizeGrid, 4), Loc.T("CtxDelete"));
        await _rec.Pause(1.2);

        _rec.Banner(L("Alles rückgängig machen", "Undo anything"), L("Jeder Schritt lässt sich rückgängig machen. Die Datei ändert sich erst beim Speichern.", "Every step can be undone. The file only changes when you save."));
        await _rec.Click(Tab(0));
        await _rec.Pause(0.4);
        await _rec.Click(ButtonOf(vm.UndoCommand), 0.5);
        await _rec.Pause(1.3);
    }

    /// <summary>
    /// A right click on a page opens its menu (the same in both modes), which stays open for a moment so its commands
    /// can be read; then the entry <paramref name="header"/> is clicked.
    /// </summary>
    private async Task ContextMenuAsync(Control owner, Point page, string header)
    {
        await _rec.RightClick(page);
        await Recorder.Settle(100);
        if (owner.ContextMenu is { IsOpen: false } menu)
            menu.Open(owner); // the headless platform may not turn the right click into a context request
        await _rec.WaitUntil(() => owner.ContextMenu?.IsOpen == true, 10, maxVideoSeconds: 0.3);
        await _rec.Pause(1.4);
        var item = _window.GetVisualDescendants().OfType<MenuItem>().First(m => m.Header as string == header && m.IsEffectivelyVisible);
        await _rec.Click(item, 0.7);
        await _rec.WaitUntil(() => owner.ContextMenu?.IsOpen != true, 10, maxVideoSeconds: 0.3);
    }

    private async Task SplitAsync()
    {
        var vm = _window.ViewModel;
        _rec.Banner(L("Teilen und extrahieren", "Split and extract"), L("Ein Dokument in mehrere Dateien teilen oder einzelne Seiten als neue Datei speichern.", "Split a document into several files or save single pages as a new file."));
        await _rec.Click(Tab(1));
        await _rec.Pause(0.4);
        await _rec.Click(ButtonOf(vm.SplitDocumentCommand));
        await DialogOpen();
        await _rec.Pause(2.6);
        await _rec.Click(DialogCancel());
        await DialogClosed();
        await _rec.Pause(0.4);
    }

    private async Task MergeAsync()
    {
        await OpenAsync(null);
        var vm = _window.ViewModel;
        _rec.Banner(L("Dateien zusammenführen", "Merge files"), L("Mehrere PDFs werden in der gewünschten Reihenfolge zu einem Dokument.", "Several PDFs become one document, in the order you want."));
        var merging = vm.OpenStartupFilesAsync([_report, _cover, _annex], merge: true);
        await DialogOpen();
        await _rec.Pause(1);
        var list = _rec.TopWindow.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        await _rec.Click(list[1]);
        await _rec.Pause(0.4);
        await _rec.Click(_rec.TopWindow.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == Loc.T("ConcatUp")));
        await _rec.Pause(1);
        await _rec.Click(DialogOk());
        await DialogClosed();
        await merging;
        await Recorder.Settle(800);
        await _rec.Click(ButtonOf(vm.ShowOrganizeCommand));
        await _rec.Pause(2.4);
    }

    private async Task OcrAsync()
    {
        await OpenAsync(_invoice);
        var vm = _window.ViewModel;
        vm.FitHeightCommand.Execute(null); // a page lower than A4 would stay small
        await Recorder.Settle(800);
        _rec.Banner(L("Text erkennen", "Recognize text"), L("Gescannte Seiten werden durchsuchbar: auf dem eigenen Rechner, Deutsch und Englisch.", "Scanned pages become searchable: on your own computer, German and English."));
        await _rec.Pause(0.8);
        await _rec.Click(_window.FindControl<RibbonButton>("OcrButton")!);
        await DialogOpen();
        await _rec.Pause(1.3);
        await _rec.Click(DialogOk());
        await DialogClosed();
        await _rec.WaitUntil(() => !vm.IsBusy, 120, maxVideoSeconds: 1.5); // the wait depends on the machine; its length says nothing here
        await _rec.Pause(0.8);

        _rec.Banner(L("Text markieren und kopieren", "Select and copy text"), L("Auch der erkannte Text eines Scans lässt sich markieren und weiterverwenden.", "The recognized text of a scan can be selected and reused, too."));
        await _rec.Click(_window.FindControl<RibbonButton>("SelectTextButton")!);
        await _rec.Pause(0.3);
        await _rec.Drag(OnPage(0, 38, 144), OnPage(0, 275, 144), seconds: 1);
        await _rec.Pause(1.4);
        await _rec.Click(ButtonOf(vm.CopyTextCommand), 0.5);
        await _rec.Pause(1.3);
        await _rec.Click(_window.FindControl<RibbonButton>("SelectTextButton")!, 0.4);
    }

    private async Task StampAsync()
    {
        var vm = _window.ViewModel;
        _rec.Banner(L("Stempeln", "Stamps"), L("Der Stempel „Eingegangen“ trägt das heutige Datum. Ein Klick setzt ihn auf die Seite.", "The “Received” stamp carries today’s date. One click puts it on the page."));
        await _rec.Click(_window.FindControl<RibbonButton>("StampButton")!);
        await _rec.Pause(1.2);
        var tile = _window.GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.Command, vm.SelectStampCommand) && (b.CommandParameter as string)?.Contains(L("Eingegangen", "Received")) == true);
        await _rec.Click(tile);
        await _rec.Pause(0.3);
        await _rec.MoveTo(OnPage(0, 340, 130), 0.9);
        await _rec.MoveTo(OnPage(0, 460, 85), 0.5);
        await _rec.Pause(0.4);
        await _rec.Click(OnPage(0, 460, 85), 0.2);
        await _rec.Pause(1.6);
    }

    private async Task DynamicStampAsync()
    {
        var vm = _window.ViewModel;
        _rec.Banner(L("Dynamische Stempel einrichten", "Set up dynamic stamps"), L("Felder aufziehen: fester Text oder ein Platzhalter für das Datum, das sich selbst einträgt.", "Draw fields: fixed text or a placeholder for the date, which fills itself in."));
        await _rec.Click(_window.FindControl<RibbonButton>("StampButton")!);
        await _rec.Pause(0.8);
        await _rec.Click(ButtonNamed(Loc.T("StampNew")));
        await DialogOpen();
        await _rec.Pause(0.8);
        var editor = (StampEditorDialog)_rec.TopWindow;
        await _rec.Click(editor.GetVisualDescendants().OfType<TextBox>().First(b => b.Text == Loc.T("StampDefaultName")));
        await _rec.Key(Key.A, RawInputModifiers.Control);
        await _rec.Type(L("Freigegeben", "Approved"));
        await _rec.Pause(0.4);

        await _rec.Drag(OnStamp(editor, 0.06, 0.12), OnStamp(editor, 0.94, 0.52), seconds: 0.7);
        await _rec.Pause(0.3);
        await _rec.Type(L("FREIGEGEBEN", "APPROVED")); // the new field's text is selected
        await _rec.Pause(0.4);
        await _rec.Click(editor.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "#0070C0"));
        await _rec.Pause(0.6);

        await _rec.Drag(OnStamp(editor, 0.14, 0.58), OnStamp(editor, 0.86, 0.88), seconds: 0.7);
        await _rec.Pause(2); // a new field holds the date placeholder, and the picture shows today's date at once
        await _rec.Click(DialogOk());
        await DialogClosed();
        await _rec.Pause(0.3);

        _rec.Banner(L("Dynamischen Stempel setzen", "Place a dynamic stamp"), L("Er liegt jetzt im Stempelordner und trägt beim Setzen das Datum des Tages ein.", "It is now in the stamp folder and enters the current date when placed."));
        await _rec.Click(_window.FindControl<RibbonButton>("StampButton")!);
        await _rec.Pause(1);
        var tile = _window.GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.Command, vm.SelectStampCommand) && (b.CommandParameter as string)?.Contains(L("Freigegeben", "Approved")) == true);
        await _rec.Click(tile);
        await _rec.Pause(0.3);
        await _rec.MoveTo(OnPage(0, 460, 150), 0.9);
        await _rec.Pause(0.4);
        await _rec.Click(OnPage(0, 460, 150), 0.2);
        await _rec.Pause(0.2);
        await _rec.MoveTo(OnPage(0, 560, 300), 0.4); // the pointer would cover the date
        await _rec.Pause(1.2);
    }

    private async Task TextAsync()
    {
        var vm = _window.ViewModel;
        _rec.Banner(L("Text einfügen", "Insert text"), L("Klicken und losschreiben. Schrift, Größe und Farbe lassen sich einstellen.", "Click and start typing. Font, size and colour can be set."));
        await _rec.Click(ButtonOf(vm.SelectTextToolCommand));
        await _rec.Pause(0.2);
        await _rec.Click(OnPage(0, 300, 390));
        await _rec.Pause(0.3);
        await _rec.Type(L("Geprüft und freigegeben", "Checked and approved"));
        await _rec.Pause(0.8);
        await _rec.Click(OnPage(0, 120, 415), 0.4);
        await _rec.Pause(1.2);
    }

    private async Task TemplatesAsync()
    {
        var templates = _window.FindControl<RibbonButton>("TemplatesButton")!;
        _rec.Banner(L("Textvorlagen einrichten", "Set up text templates"), L("Häufige Texte einmal anlegen. Platzhalter wie {$DATE} und {$FILE} füllt das Programm aus.", "Create frequent texts once. The program fills in placeholders like {$DATE} and {$FILE}."));
        await _rec.Click(templates);
        await _rec.Pause(0.8);
        await _rec.Click(ButtonNamed(Loc.T("TemplatesEdit")));
        await DialogOpen();
        await _rec.Pause(0.8);
        await _rec.Click(ButtonNamed(Loc.T("TemplateAdd")));
        await _rec.Pause(0.3);
        await _rec.Type(L("Geprüft", "Checked")); // the new template's name is selected
        await _rec.Pause(0.3);
        await _rec.Click(_rec.TopWindow.GetVisualDescendants().OfType<TextBox>().First(b => b.AcceptsReturn));
        await _rec.Pause(0.2);
        await _rec.Type(L("Geprüft am {$DATE}: {$FILE}", "Checked on {$DATE}: {$FILE}"));
        await _rec.Pause(1.2);
        await _rec.Click(DialogOk());
        await DialogClosed();
        await _rec.Pause(0.3);

        _rec.Banner(L("Textvorlage einsetzen", "Insert a text template"), L("Der Text folgt dem Zeiger, ein Klick setzt ihn: mit dem Datum und dem Dateinamen.", "The text follows the pointer, one click places it: with the date and the file name."));
        await _rec.Click(templates);
        await _rec.Pause(0.9);
        await _rec.Click(ButtonNamed(L("Geprüft", "Checked")));
        await _rec.Pause(0.3);
        await _rec.MoveTo(OnPage(0, 180, 300), 0.8);
        await _rec.MoveTo(OnPage(0, 300, 358), 0.7);
        await _rec.Pause(0.5);
        await _rec.Click(OnPage(0, 300, 358), 0.2);
        await _rec.Pause(0.4);
        await _rec.MoveTo(OnPage(0, 120, 415), 0.4);
        await _rec.Pause(1.5);
    }

    private async Task RedactAsync()
    {
        _rec.Banner(L("Schwärzen", "Black out"), L("Beim Speichern wird der Inhalt unter dem Balken endgültig aus der Datei entfernt.", "Saving removes the content under the box from the file for good."));
        await _rec.Click(_window.FindControl<RibbonButton>("RedactButton")!);
        await _rec.Pause(0.3);
        await _rec.Drag(OnPage(0, 76, 334), OnPage(0, 290, 351), seconds: 0.9);
        await _rec.Pause(0.3);
        await _rec.Drag(OnPage(0, 38, 72), OnPage(0, 150, 119), seconds: 0.8);
        await _rec.MoveTo(OnPage(0, 480, 200), 0.5);
        await _rec.Pause(1.2);
        await _rec.Key(Key.Escape);
        await _rec.Pause(0.5);
    }

    private async Task ThemeAsync()
    {
        await OpenAsync(_report);
        _rec.Banner(L("Hell oder dunkel", "Light or dark"), L("Das Programm folgt dem System oder Ihrer Wahl, auf Deutsch oder Englisch.", "The program follows the system or your choice, in English or German."));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Recorder.Settle(400);
        await _rec.Pause(3);
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
    }
}
