using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EasyPDF.App.Controls;
using EasyPDF.App.Services;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Stamps;
using EasyPDF.Core.Templates;
using Xunit;

namespace EasyPDF.App.Tests;

public class StampFieldTests
{
    private static readonly TemplateContext Context = new(new DateTime(2019, 1, 11), "wolfgang", "post.pdf", 2, 5);

    internal static async Task Pump(int ms = 300)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    /// <summary>A transparent PNG with a blue bar across the top.</summary>
    internal static byte[] Picture(int width = 300, int height = 100)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
            for (var y = 0; y < height / 4; y++)
                for (var x = 0; x < width; x++)
                    System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address + y * fb.RowBytes + x * 4, unchecked((int)0xFF0000FF));
        using var png = new MemoryStream();
        bitmap.Save(png, new PngBitmapEncoderOptions());
        return png.ToArray();
    }

    internal static readonly StampDefinition RedDate = new()
    {
        Fields = [new StampField { X = 0.1, Y = 0.5, Width = 0.8, Height = 0.3, Text = "{$DATE:dd.MM.yyyy}", Color = "#FF0000" }],
    };

    internal static (int Left, int Top, int Right, int Bottom, int Count) RedInk(byte[] bgra, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1, count = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var p = (y * width + x) * 4;
                if (bgra[p + 3] < 128 || bgra[p + 2] < 180 || bgra[p] > 100)
                    continue;
                count++;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        return (left, top, right, bottom, count);
    }

    [AvaloniaFact]
    public void TheTextIsDrawnInsideItsAreaAndKeepsThePicture()
    {
        var result = StampComposer.Compose(Picture(), RedDate, Context);

        Assert.Equal((300, 100), (result.PixelWidth, result.PixelHeight));
        var ink = RedInk(result.Bgra, 300, 100);
        Assert.True(ink.Count > 200, $"red pixels: {ink.Count}");
        Assert.InRange(ink.Top, 49, 56);
        Assert.InRange(ink.Bottom, 74, 80);
        Assert.InRange(ink.Left, 30, 150);
        Assert.InRange(ink.Right, 150, 270);
        Assert.InRange((ink.Left + ink.Right) / 2.0, 145, 155); // centred
        // The picture itself: blue bar at the top, transparent elsewhere
        Assert.Equal(255, result.Bgra[(10 * 300 + 150) * 4]);
        Assert.Equal(255, result.Bgra[(10 * 300 + 150) * 4 + 3]);
        Assert.Equal(0, result.Bgra[(35 * 300 + 150) * 4 + 3]);
    }

    [AvaloniaFact]
    public void TheEdgesOfTheTextKeepItsColourOnATransparentPicture()
    {
        var result = StampComposer.Compose(Picture(), RedDate, Context);

        // No coloured fringes: half transparent edge pixels are pure red as well (below the blue bar of the picture)
        var edges = 0;
        for (var y = 40; y < 100; y++)
            for (var x = 0; x < 300; x++)
            {
                var p = (y * 300 + x) * 4;
                var a = result.Bgra[p + 3];
                if (a is < 40 or > 200)
                    continue;
                edges++;
                Assert.True(result.Bgra[p] < 30 && result.Bgra[p + 1] < 30 && result.Bgra[p + 2] > 225, $"pixel {x},{y}: {result.Bgra[p + 2]},{result.Bgra[p + 1]},{result.Bgra[p]},{a}");
            }
        Assert.True(edges > 20, $"edge pixels: {edges}");
    }

    [AvaloniaTheory]
    [InlineData(StampFieldAlign.Left)]
    [InlineData(StampFieldAlign.Right)]
    public void ATextCanBeAlignedAndATooLongOneIsReduced(StampFieldAlign align)
    {
        var field = new StampField { X = 0.2, Y = 0.5, Width = 0.6, Height = 0.4, Text = "1", Color = "#FF0000", Align = align };
        var ink = RedInk(StampComposer.Compose(Picture(), new StampDefinition { Fields = [field] }, Context).Bgra, 300, 100);
        if (align == StampFieldAlign.Left)
            Assert.InRange(ink.Left, 60, 64);
        else
            Assert.InRange(ink.Right, 235, 240);

        var longText = field with { Text = new string('W', 60) };
        var wide = RedInk(StampComposer.Compose(Picture(), new StampDefinition { Fields = [longText] }, Context).Bgra, 300, 100);
        Assert.InRange(wide.Left, 58, 66);
        Assert.InRange(wide.Right, 234, 241);
    }

    [AvaloniaFact]
    public void AMonthDoesNotChangeTheSizeOfTheText()
    {
        static int HeightOf(string month)
        {
            var field = new StampField { X = 0, Y = 0.5, Width = 1, Height = 0.3, Text = month, Color = "#FF0000" };
            var ink = RedInk(StampComposer.Compose(Picture(), new StampDefinition { Fields = [field] }, Context).Bgra, 300, 100);
            return ink.Bottom - ink.Top;
        }

        Assert.InRange(HeightOf("JAN"), 28, 31);
        Assert.InRange(HeightOf("NOV"), 28, 31);
        // Grows above the capitals by the dots of the umlaut, doesn't shrink to fit them
        Assert.InRange(HeightOf("MÄR"), 31, 42);
    }

    [AvaloniaFact]
    public void ABadColourIsDrawnBlackInsteadOfFailing()
    {
        var field = RedDate.Fields[0] with { Color = "no colour" };
        var result = StampComposer.Compose(Picture(), new StampDefinition { Fields = [field] }, Context);

        Assert.Contains(Enumerable.Range(0, 300 * 100), i => result.Bgra[i * 4 + 3] == 255 && result.Bgra[i * 4 + 2] == 0 && result.Bgra[i * 4] == 0);
    }

    internal static async Task<(MainWindow Window, HorizontalPageView View, string Stamp)> OpenAsync(TempDir dir)
    {
        var folder = Directory.CreateDirectory(dir.File("stamps")).FullName;
        var path = Path.Combine(folder, "Received.png");
        File.WriteAllBytes(path, StampFile.WriteDpi(StampFile.WriteDefinition(Picture(), RedDate), 96));
        var settings = new AppSettings { Language = "en", StampFolder = folder };
        AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3, widthBase: 500));
        await Pump();
        return (window, window.FindControl<HorizontalPageView>("PageView")!, path);
    }

    internal static Point MiddleOfPage(Window window, HorizontalPageView view)
    {
        var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
        var y = view.Bounds.Height / 2;
        for (var x = view.Bounds.Width / 2; x > 0; x -= 2)
            if (view.PageIndexAt(new Point(x, y)) == 0)
                return origin + new Point(x, y);
        throw new InvalidOperationException("No page found.");
    }

    internal static async Task PlaceAsync(MainWindow window, HorizontalPageView view, string path)
    {
        window.ViewModel.SelectStampCommand.Execute(path);
        await Pump();
        Assert.NotNull(view.PendingStampImage);
        var p = MiddleOfPage(window, view);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();
    }

    [AvaloniaFact]
    public async Task PlacingAStampWithFieldsWritesTodaysDateAndKeepsWhatItWasMadeFrom()
    {
        using var dir = new TempDir();
        var (window, view, path) = await OpenAsync(dir);
        var vm = window.ViewModel;
        var file = File.ReadAllBytes(path);

        await PlaceAsync(window, view, path);

        var stamp = Assert.Single(vm.Document!.Pages[0].Stamps);
        Assert.NotEqual(file, stamp.Image);
        Assert.True(RedInk(stamp.Bgra!, stamp.PixelWidth, stamp.PixelHeight).Count > 200);
        Assert.Equal(file, stamp.Template!.Picture);
        Assert.Equal(DateTime.Today, stamp.Template.Context.Now.Date);
        Assert.Equal(1, stamp.Template.Context.Page);
        Assert.Equal(225, stamp.Rect.Width, 0); // 300 px at 96 dpi
        window.Close();
    }

    [AvaloniaFact]
    public async Task APlainPictureStaysExactlyAsItIs()
    {
        using var dir = new TempDir();
        var (window, view, path) = await OpenAsync(dir);
        var plain = Picture();
        File.WriteAllBytes(path, plain);

        await PlaceAsync(window, view, path);

        var stamp = Assert.Single(window.ViewModel.Document!.Pages[0].Stamps);
        Assert.Equal(plain, stamp.Image);
        Assert.Null(stamp.Template);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AStampWithFieldsIsSavedWithItsTextOnThePage()
    {
        using var dir = new TempDir();
        var (window, view, path) = await OpenAsync(dir);
        var vm = window.ViewModel;
        await PlaceAsync(window, view, path);

        await vm.SaveCommand.ExecuteAsync(null);
        await Pump();

        Assert.False(vm.IsDirty);
        // Written into the page: red ink where the stamp was, after the file was opened again
        var page = vm.Document!.Pages[0];
        Assert.Empty(page.Stamps);
        var rendered = await page.Source.RenderAsync(page.SourceIndex, (int)page.BaseSize.Width, (int)page.BaseSize.Height);
        var reds = 0;
        for (var i = 0; i + 3 < rendered.Pixels.Length; i += 4)
            if (rendered.Pixels[i + 2] > 200 && rendered.Pixels[i + 1] < 80 && rendered.Pixels[i] < 80)
                reds++;
        Assert.True(reds > 50, $"red pixels: {reds}");
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheGalleryShowsAStampWithFieldsAsItWouldLookToday()
    {
        using var dir = new TempDir();
        var (window, _, _) = await OpenAsync(dir);
        var button = window.FindControl<EasyPDF.App.Controls.Ribbon.RibbonButton>("StampButton")!;
        var flyout = Assert.IsType<Flyout>(button.Flyout);

        flyout.ShowAt(button);
        await Pump();

        var host = (StackPanel)((ScrollViewer)flyout.Content!).Content!;
        var tile = ((WrapPanel)host.Children[0]).Children.OfType<Button>().Single();
        var image = (Bitmap)((Image)((StackPanel)tile.Content!).Children[0]).Source!;
        var ink = RedInk(StampPixels.ToStraightBgra(image), image.PixelSize.Width, image.PixelSize.Height);
        Assert.True(ink.Count > 30, $"red pixels: {ink.Count}");
        window.Close();
    }

    [AvaloniaFact]
    public void TheSampleStampsHaveADateField()
    {
        foreach (var (file, language) in new[] { ("Eingegangen.png", "de"), ("Received.png", "en") })
        {
            var data = File.ReadAllBytes(Path.Combine(StampLibrary.SampleFolder, file));
            var definition = StampFile.ReadDefinition(data);
            Assert.NotNull(definition);
            Assert.True(definition.UsesDate);
            Assert.Equal(language, Assert.Single(definition.Fields).Language);
            Assert.Equal(300, StampLibrary.ReadDpi(data)!.Value, 0);
        }
    }
}
