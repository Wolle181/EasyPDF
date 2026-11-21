using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EasyPDF.App.Controls;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Stamps;
using EasyPDF.Core.Templates;
using Xunit;
using static EasyPDF.App.Tests.StampFieldTests;

namespace EasyPDF.App.Tests;

public class StampEditorTests
{
    private static readonly TemplateContext Context = new(new DateTime(2019, 1, 11), "wolfgang", "post.pdf", 1, 1);

    /// <summary>A 40 x 20 JPG with 150 dpi: white with a black block in the middle.</summary>
    private static readonly byte[] Jpg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAlgCWAAD/2wBDAAIBAQEBAQIBAQECAgICAgQDAgICAgUEBAMEBgUGBgYFBgYGBwkIBgcJBwYGCAsICQoKCgoKBggLDAsKDAkKCgr/2wBDAQICAgICAgUDAwUKBwYHCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgr/wAARCAAUACgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD9/K/OD9vX/g6B/YF/4J2/tY+K/wBjr41/CL4wap4n8H/Yf7TvvC2gaVPYS/a7C3vo/KefUoZGxFcxht0a4YMBkAMf0fr+QL/g6O/5Tr/HP/uWf/UY0mgD9f8A/iNW/wCCWX/RA/2gP/CW0P8A+XFdf+z1/wAHdf8AwTb/AGlPj74H/Zz8C/BL44Wmt+P/ABhpnhvRrrVvDejx2sN1fXUdrE8zR6q7rEHlUsVR2CgkKx4P8qVe/wD/AASd/wCUpv7NP/ZwHg3/ANPlnQB/b7RRRQAUUUUAFFFFABRRRQB//9k=");

    /// <summary>The editor on its own window, big enough for everything in it.</summary>
    private static async Task<StampEditorDialog> OpenEditorAsync(string? path = null)
    {
        AppEnvironment.Apply(new AppSettings { Language = "en" });
        var dialog = new StampEditorDialog(path, Context) { Height = 700 };
        dialog.Show();
        await Pump();
        return dialog;
    }

    /// <summary>A window point at a share of the picture in the canvas.</summary>
    private static Point At(StampEditorDialog dialog, double x, double y)
    {
        var area = dialog.Canvas.PictureArea;
        return dialog.Canvas.TranslatePoint(new Point(area.X + x * area.Width, area.Y + y * area.Height), dialog)!.Value;
    }

    private static async Task Drag(Window window, Point from, Point to)
    {
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        await Pump(100);
    }

    private static T Named<T>(Window dialog, string key) where T : ContentControl =>
        dialog.GetLogicalDescendants().OfType<T>().Single(b => b.Content as string == Loc.T(key));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static TextBox FieldTextBox(StampEditorDialog dialog)
    {
        // The text box under the caption "Text of the field"
        var panel = dialog.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Children.OfType<TextBlock>().Any(t => t.Text == Loc.T("StampFieldText")));
        return panel.Children.OfType<TextBox>().First();
    }

    private static TextBox NameBox(StampEditorDialog dialog)
    {
        var row = dialog.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Children.OfType<TextBlock>().Any(t => t.Text == Loc.T("StampEditorName")));
        return row.Children.OfType<TextBox>().Single();
    }

    /// <summary>Runs the dialog as the program does and presses OK at the end; returns its result.</summary>
    private static async Task<StampEditorResult?> RunAsync(string? path, Func<StampEditorDialog, Task> work, bool ok = true)
    {
        AppEnvironment.Apply(new AppSettings { Language = "en" });
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var dialog = new StampEditorDialog(path, Context) { Height = 700 };
        var result = dialog.ShowDialog<StampEditorResult?>(owner);
        await Pump();
        await work(dialog);
        Click(dialog.GetLogicalDescendants().OfType<Button>().First(b => ok ? b.IsDefault : b.IsCancel));
        await Pump();
        owner.Close();
        return await result;
    }

    [AvaloniaFact]
    public async Task ANewStampIsAnEmptyAreaWithAFieldDrawnOnIt()
    {
        var result = await RunAsync(null, async dialog =>
        {
            Assert.Empty(dialog.Fields);
            await Drag(dialog, At(dialog, 0.1, 0.5), At(dialog, 0.9, 0.8));
            var field = Assert.Single(dialog.Fields);
            Assert.Equal(0.1, field.X, 1);
            Assert.Equal(0.5, field.Y, 1);
            Assert.Equal(0.8, field.Width, 1);
            Assert.Equal(0.3, field.Height, 1);
            Assert.Equal(0, dialog.Canvas.SelectedIndex);
            FieldTextBox(dialog).Text = "{$DATE:dd.MM.yyyy}";
            await Pump(100);
        });

        Assert.NotNull(result);
        Assert.Equal(Loc.T("StampDefaultName"), result.Name);
        var definition = StampFile.ReadDefinition(result.Png);
        Assert.Equal("{$DATE:dd.MM.yyyy}", Assert.Single(definition!.Fields).Text);
        Assert.Equal(300, StampLibrary.ReadDpi(result.Png)!.Value, 0);
        using var stream = new MemoryStream(result.Png);
        using var bitmap = new Bitmap(stream);
        Assert.Equal(new PixelSize(709, 295), bitmap.PixelSize);
        Assert.Equal(0, StampPixels.ToStraightBgra(bitmap)[3]); // an empty area is transparent, without the text of the fields
        Assert.DoesNotContain(StampPixels.ToStraightBgra(bitmap), b => b != 0);
    }

    [AvaloniaFact]
    public async Task ThePreviewShowsTheTextWithTheDateOfTheContext()
    {
        var dialog = await OpenEditorAsync();
        await Drag(dialog, At(dialog, 0.1, 0.2), At(dialog, 0.9, 0.8));
        // The default colour is red
        var ink = RedInk(StampPixels.ToStraightBgra(dialog.Canvas.Picture!), dialog.Canvas.Picture!.PixelSize.Width, dialog.Canvas.Picture.PixelSize.Height);
        Assert.True(ink.Count > 500, $"red pixels: {ink.Count}");

        FieldTextBox(dialog).Text = "";
        await Pump(100);
        ink = RedInk(StampPixels.ToStraightBgra(dialog.Canvas.Picture!), dialog.Canvas.Picture!.PixelSize.Width, dialog.Canvas.Picture.PixelSize.Height);
        Assert.Equal(0, ink.Count);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task AFieldCanBeMovedResizedAndRemoved()
    {
        var dialog = await OpenEditorAsync();
        await Drag(dialog, At(dialog, 0.2, 0.2), At(dialog, 0.5, 0.5));

        await Drag(dialog, At(dialog, 0.35, 0.35), At(dialog, 0.55, 0.45)); // moved by 0.2 and 0.1
        var moved = Assert.Single(dialog.Fields);
        Assert.Equal(0.4, moved.X, 1);
        Assert.Equal(0.3, moved.Y, 1);
        Assert.Equal(0.3, moved.Width, 1);

        await Drag(dialog, At(dialog, moved.X + moved.Width, moved.Y + moved.Height), At(dialog, 0.9, 0.9)); // bottom right corner
        var resized = Assert.Single(dialog.Fields);
        Assert.Equal(0.4, resized.X, 1);
        Assert.Equal(0.5, resized.Width, 1);
        Assert.Equal(0.6, resized.Height, 1);

        // Pulled beyond the picture it stays inside
        await Drag(dialog, At(dialog, 0.65, 0.6), At(dialog, 5, 5));
        var kept = Assert.Single(dialog.Fields);
        Assert.Equal(1, kept.X + kept.Width, 3);
        Assert.Equal(1, kept.Y + kept.Height, 3);

        // A click on free space selects nothing and adds no field
        await Drag(dialog, At(dialog, 0.05, 0.05), At(dialog, 0.05, 0.05));
        Assert.Single(dialog.Fields);
        Assert.Equal(-1, dialog.Canvas.SelectedIndex);

        await Drag(dialog, At(dialog, kept.X + 0.1, kept.Y + 0.1), At(dialog, kept.X + 0.1, kept.Y + 0.1));
        Assert.Equal(0, dialog.Canvas.SelectedIndex);
        dialog.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        await Pump(100);
        Assert.Empty(dialog.Fields);
        Assert.Equal(-1, dialog.Canvas.SelectedIndex);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task EditingAStampKeepsItsPictureAndFields()
    {
        using var dir = new TempDir();
        var path = dir.File("Received.png");
        var file = StampFile.WriteDpi(StampFile.WriteDefinition(Picture(), RedDate), 200);
        File.WriteAllBytes(path, file);

        var result = await RunAsync(path, async dialog =>
        {
            Assert.Equal(RedDate.Fields, dialog.Fields);
            Assert.Equal("Received", NameBox(dialog).Text);
            Assert.Equal(0, dialog.Canvas.SelectedIndex);
            FieldTextBox(dialog).Text = "{$USER}";
            await Pump(100);
        });

        Assert.Equal("Received", result!.Name);
        Assert.Equal(200, StampLibrary.ReadDpi(result.Png)!.Value, 0);
        Assert.Equal("{$USER}", Assert.Single(StampFile.ReadDefinition(result.Png)!.Fields).Text);
        // The picture stays without the text: the result is the same file with another definition
        Assert.Equal(StampFile.WriteDefinition(result.Png, null), StampFile.WriteDefinition(file, null));
    }

    [AvaloniaFact]
    public async Task CancellingOrClosingGivesNoResult()
    {
        Assert.Null(await RunAsync(null, async dialog => await Drag(dialog, At(dialog, 0.1, 0.1), At(dialog, 0.9, 0.9)), ok: false));

        AppEnvironment.Apply(new AppSettings { Language = "en" });
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var dialog = new StampEditorDialog(null, Context);
        var result = dialog.ShowDialog<StampEditorResult?>(owner);
        await Pump(100);
        dialog.Close();
        await Pump(100);
        Assert.Null(await result);
        owner.Close();
    }

    /// <summary>A 40 x 20 PNG with 150 dpi: white with a black block in the middle.</summary>
    private static byte[] WhitePng()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(40, 20), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
            for (var y = 0; y < 20; y++)
                for (var x = 0; x < 40; x++)
                    System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address + y * fb.RowBytes + x * 4, x is >= 10 and < 30 && y is >= 5 and < 15 ? unchecked((int)0xFF000000) : -1);
        using var png = new MemoryStream();
        bitmap.Save(png, new PngBitmapEncoderOptions());
        return StampFile.WriteDpi(png.ToArray(), 150);
    }

    [AvaloniaFact]
    public async Task TheWhiteOfAPngCanBeMadeTransparent()
    {
        using var dir = new TempDir();
        var path = dir.File("scan.png");
        File.WriteAllBytes(path, WhitePng());

        var kept = await RunAsync(null, dialog =>
        {
            dialog.UsePicture(path);
            Assert.Equal("scan", NameBox(dialog).Text);
            return Task.CompletedTask;
        });
        Assert.Equal(150, StampLibrary.ReadDpi(kept!.Png)!.Value, 0);
        Assert.Equal(255, Alpha(kept.Png, 2, 2));

        var transparent = await RunAsync(null, async dialog =>
        {
            dialog.UsePicture(path);
            Named<CheckBox>(dialog, "StampEditorWhite").IsChecked = true;
            await Pump(100);
        });
        Assert.Equal(0, Alpha(transparent!.Png, 2, 2));
        Assert.Equal(255, Alpha(transparent.Png, 20, 10)); // the black block stays

        static byte Alpha(byte[] png, int x, int y)
        {
            using var stream = new MemoryStream(png);
            using var bitmap = new Bitmap(stream);
            Assert.Equal(new PixelSize(40, 20), bitmap.PixelSize);
            return StampPixels.ToStraightBgra(bitmap)[(y * 40 + x) * 4 + 3];
        }
    }

    [AvaloniaFact]
    public async Task AJpgIsRefusedAndTheCurrentPictureStays()
    {
        using var dir = new TempDir();
        var jpg = dir.File("scan.jpg");
        File.WriteAllBytes(jpg, Jpg);
        var dialog = await OpenEditorAsync();
        var before = dialog.Canvas.Picture;

        dialog.UsePicture(jpg);
        await Pump(100);

        Assert.Same(before, dialog.Canvas.Picture);
        Assert.Equal(Loc.T("StampDefaultName"), NameBox(dialog).Text);
        Assert.Contains(dialog.GetLogicalDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == Loc.F("StampEditorPngOnly", "scan.jpg"));

        // A good picture afterwards takes the message away
        var good = dir.File("good.png");
        File.WriteAllBytes(good, WhitePng());
        dialog.UsePicture(good);
        await Pump(100);
        Assert.NotSame(before, dialog.Canvas.Picture);
        Assert.DoesNotContain(dialog.GetLogicalDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == Loc.F("StampEditorPngOnly", "scan.jpg"));
        dialog.Close();
    }

    [AvaloniaFact]
    public void TheEditorDoesNotOpenAFileThatIsNoPng()
    {
        using var dir = new TempDir();
        var disguised = dir.File("scan.png"); // a JPG under the wrong name
        File.WriteAllBytes(disguised, Jpg);
        AppEnvironment.Apply(new AppSettings { Language = "en" });

        Assert.Throws<InvalidDataException>(() => new StampEditorDialog(disguised, Context));
    }

    [AvaloniaFact]
    public async Task ADamagedPictureIsReportedAndTheCurrentOneStays()
    {
        using var dir = new TempDir();
        var bad = dir.File("bad.png");
        File.WriteAllBytes(bad, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]); // the start of a PNG, nothing behind it
        var dialog = await OpenEditorAsync();
        var before = dialog.Canvas.Picture;

        dialog.UsePicture(bad);
        await Pump(100);

        Assert.Same(before, dialog.Canvas.Picture);
        Assert.Contains(dialog.GetLogicalDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == Loc.F("StampUnreadable", "bad.png"));
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task OkNeedsAName()
    {
        var dialog = await OpenEditorAsync();
        var ok = dialog.GetLogicalDescendants().OfType<Button>().First(b => b.IsDefault);
        Assert.True(ok.IsEnabled);
        NameBox(dialog).Text = "   ";
        await Pump(100);
        Assert.False(ok.IsEnabled);
        dialog.Close();
    }

    private static async Task<(MainWindow Window, string Folder, Flyout Gallery)> WindowWithGalleryAsync(TempDir dir)
    {
        var folder = Directory.CreateDirectory(dir.File("stamps")).FullName;
        var settings = new AppSettings { Language = "en", StampFolder = folder };
        AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        var button = window.FindControl<EasyPDF.App.Controls.Ribbon.RibbonButton>("StampButton")!;
        return (window, folder, Assert.IsType<Flyout>(button.Flyout));
    }

    private static void ShowGallery(MainWindow window, Flyout flyout) =>
        flyout.ShowAt(window.FindControl<EasyPDF.App.Controls.Ribbon.RibbonButton>("StampButton")!);

    private static StackPanel Host(Flyout flyout) => (StackPanel)((ScrollViewer)flyout.Content!).Content!;

    [AvaloniaFact]
    public async Task NewStampInTheGalleryOpensTheEditorAndSavesIntoTheFolder()
    {
        using var dir = new TempDir();
        var (window, folder, flyout) = await WindowWithGalleryAsync(dir);
        ShowGallery(window, flyout);
        await Pump();

        Click(Host(flyout).Children.OfType<Button>().Single(b => b.Content as string == Loc.T("StampNew")));
        await Pump();

        var dialog = Assert.Single(window.OwnedWindows.OfType<StampEditorDialog>());
        NameBox(dialog).Text = "Mine: new";
        await Pump(100);
        await Drag(dialog, At(dialog, 0.1, 0.1), At(dialog, 0.9, 0.9));
        Click(dialog.GetLogicalDescendants().OfType<Button>().First(b => b.IsDefault));
        await Pump();

        var saved = Assert.Single(Directory.GetFiles(folder));
        Assert.Equal("Mine_ new.png", Path.GetFileName(saved));
        Assert.Single(StampFile.ReadDefinition(File.ReadAllBytes(saved))!.Fields);
        Assert.Contains("Mine_ new", window.ViewModel.StatusMessage);

        // The gallery shows it the next time it opens
        ShowGallery(window, flyout);
        await Pump();
        Assert.Single(((WrapPanel)Host(flyout).Children[0]).Children.OfType<Button>());
        window.Close();
    }

    /// <summary>The web version's library: shared stamps are those in a folder named "Shared".</summary>
    private sealed class FakeLibrary : IUserLibrary
    {
        public int Changes { get; private set; }

        public void Prepare(AppSettings settings)
        {
        }

        public void Changed() => Changes++;

        public bool IsReadOnly(string path) => Path.GetFileName(Path.GetDirectoryName(path)) == "Shared";

        public Task ExportAsync(TopLevel host) => Task.CompletedTask;

        public Task<bool> ImportAsync(TopLevel host) => Task.FromResult(false);
    }

    [AvaloniaFact]
    public async Task OnTheWebTheGalleryDeletesOwnStampsKeepsSharedOnesAndOffersExportInsteadOfTheFolder()
    {
        using var dir = new TempDir();
        var library = new FakeLibrary();
        PlatformFeatures.Library = library;
        try
        {
            var (window, folder, flyout) = await WindowWithGalleryAsync(dir);
            var mine = Path.Combine(folder, "Mine.png");
            File.WriteAllBytes(mine, Picture());
            Directory.CreateDirectory(Path.Combine(folder, "Shared"));
            File.WriteAllBytes(Path.Combine(folder, "Shared", "Theirs.png"), Picture());
            ShowGallery(window, flyout);
            await Pump();

            var buttons = Host(flyout).Children.OfType<Button>().Select(b => b.Content as string).ToList();
            Assert.Contains(Loc.T("LibraryExport"), buttons);
            Assert.Contains(Loc.T("LibraryImport"), buttons);
            Assert.DoesNotContain(Loc.T("StampOpenFolder"), buttons);
            var shared = Host(flyout).Children.OfType<Expander>().Single();
            var theirs = ((WrapPanel)shared.Content!).Children.OfType<Button>().Single();
            Assert.Single(theirs.ContextMenu!.Items);                            // edit only

            var tile = ((WrapPanel)Host(flyout).Children[0]).Children.OfType<Button>().Single();
            var delete = tile.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header as string == Loc.T("StampDelete"));
            delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Pump();
            var question = Assert.Single(window.OwnedWindows.OfType<QuestionDialog>());
            Click(question.FindControl<Button>("YesButton")!);
            await Pump();

            Assert.False(File.Exists(mine));
            Assert.Equal(1, library.Changes);
            window.Close();
        }
        finally
        {
            PlatformFeatures.Library = null;
        }
    }

    [AvaloniaFact]
    public async Task EditingFromTheGalleryReplacesAPngAndRenamingRemovesTheOldFile()
    {
        using var dir = new TempDir();
        var (window, folder, flyout) = await WindowWithGalleryAsync(dir);
        var path = Path.Combine(folder, "Old.png");
        File.WriteAllBytes(path, StampFile.WriteDefinition(Picture(), RedDate));
        ShowGallery(window, flyout);
        await Pump();

        var tile = ((WrapPanel)Host(flyout).Children[0]).Children.OfType<Button>().Single();
        tile.ContextMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Pump();

        var dialog = Assert.Single(window.OwnedWindows.OfType<StampEditorDialog>());
        Assert.Equal(RedDate.Fields, dialog.Fields);
        NameBox(dialog).Text = "New";
        Click(dialog.GetLogicalDescendants().OfType<Button>().First(b => b.IsDefault));
        await Pump();

        Assert.Equal("New.png", Path.GetFileName(Assert.Single(Directory.GetFiles(folder))));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ARealRightClickOnAStampOpensItsMenuOverTheGalleryAndItsEntryOpensTheEditor()
    {
        using var dir = new TempDir();
        var (window, folder, flyout) = await WindowWithGalleryAsync(dir);
        File.WriteAllBytes(Path.Combine(folder, "Old.png"), StampFile.WriteDefinition(Picture(), RedDate));
        ShowGallery(window, flyout);
        await Pump();
        var tile = ((WrapPanel)Host(flyout).Children[0]).Children.OfType<Button>().Single();
        var galleryRoot = TopLevel.GetTopLevel(tile)!;
        var center = tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 2), galleryRoot)!.Value;

        galleryRoot.MouseMove(center);
        galleryRoot.MouseDown(center, MouseButton.Right);
        galleryRoot.MouseUp(center, MouseButton.Right);
        await Pump();

        Assert.True(tile.ContextMenu!.IsOpen);
        Assert.True(flyout.IsOpen); // the menu doesn't count as a click outside the gallery
        Assert.Null(window.ViewModel.PendingStampPath); // and the right click didn't pick the stamp

        var entry = tile.ContextMenu.Items.OfType<MenuItem>().Single();
        var menuRoot = TopLevel.GetTopLevel(entry)!;
        var onEntry = entry.TranslatePoint(new Point(entry.Bounds.Width / 2, entry.Bounds.Height / 2), menuRoot)!.Value;
        menuRoot.MouseMove(onEntry);
        menuRoot.MouseDown(onEntry, MouseButton.Left);
        menuRoot.MouseUp(onEntry, MouseButton.Left);
        await Pump();

        Assert.False(flyout.IsOpen);
        var dialog = Assert.Single(window.OwnedWindows.OfType<StampEditorDialog>());
        Assert.Equal(RedDate.Fields, dialog.Fields);
        dialog.Close();
        await Pump(100);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheGalleryShowsOnlyPngAndSaysSo()
    {
        using var dir = new TempDir();
        var (window, folder, flyout) = await WindowWithGalleryAsync(dir);
        File.WriteAllBytes(Path.Combine(folder, "Good.png"), Picture());
        File.WriteAllBytes(Path.Combine(folder, "Scan.jpg"), Jpg);
        File.WriteAllBytes(Path.Combine(folder, "Disguised.png"), Jpg); // a JPG renamed by hand counts as unreadable
        ShowGallery(window, flyout);
        await Pump();

        var host = Host(flyout);
        var tile = Assert.Single(((WrapPanel)host.Children[0]).Children.OfType<Button>());
        Assert.Equal(Path.Combine(folder, "Good.png"), tile.CommandParameter);
        Assert.NotNull(tile.ContextMenu);
        Assert.Contains(host.Children.OfType<TextBlock>(), t => t.Text == Loc.T("StampPngHint"));
        Assert.Contains(host.Children.OfType<TextBlock>(), t => t.Text == Loc.F("StampSkipped", 1));
        window.Close();
    }

    [AvaloniaFact]
    public async Task AJpgRenamedToPngAfterTheGalleryWasReadIsNotPlaced()
    {
        using var dir = new TempDir();
        var (window, folder, _) = await WindowWithGalleryAsync(dir);
        var path = Path.Combine(folder, "Disguised.png");
        File.WriteAllBytes(path, Jpg);

        window.ViewModel.SelectStampCommand.Execute(path);
        await Pump();

        Assert.Null(window.ViewModel.PendingStampPath);
        Assert.Null(window.FindControl<HorizontalPageView>("PageView")!.PendingStampImage);
        var message = Assert.Single(window.OwnedWindows);
        message.Close();
        await Pump(100);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheEditorAndTheSettingsSayThatStampsArePng()
    {
        var dialog = await OpenEditorAsync();
        Assert.Contains(dialog.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text!.Contains(Loc.T("StampPngHint")));
        dialog.Close();

        var settings = new AppSettings { Language = "en" };
        var window = new SettingsWindow(settings, () => { });
        window.Show();
        await Pump(100);
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("StampPngHint"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task AStampSavedByTheEditorCanBePlacedAndShowsItsDate()
    {
        using var dir = new TempDir();
        var (window, view, _) = await OpenAsync(dir);
        var path = Path.Combine(StampLibrary.ResolveFolder(new AppSettings { StampFolder = dir.File("stamps") }), "Made.png");

        var result = await RunAsync(null, async dialog => await Drag(dialog, At(dialog, 0.1, 0.1), At(dialog, 0.9, 0.9)));
        File.WriteAllBytes(path, result!.Png);

        await PlaceAsync(window, view, path);
        var stamp = window.ViewModel.Document!.Pages[0].Stamps.Single();
        Assert.True(RedInk(stamp.Bgra!, stamp.PixelWidth, stamp.PixelHeight).Count > 300);
        Assert.Contains(DateTime.Today.ToString("d"), window.ViewModel.StatusMessage);
        window.Close();
    }
}
