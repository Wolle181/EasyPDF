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
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class StampPlacementTests
{
    private static async Task Pump(int ms = 300)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    /// <summary>A solid magenta picture, saved as PNG.</summary>
    private static string MagentaPng(TempDir dir, string name = "stamp.png", int width = 60, int height = 30)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    System.Runtime.InteropServices.Marshal.WriteInt32(fb.Address + y * fb.RowBytes + x * 4, unchecked((int)0xFFFF00FF));
        var path = dir.File(name);
#pragma warning disable CS0618
        bitmap.Save(path);
#pragma warning restore CS0618
        return path;
    }

    /// <summary>Pixels that are clearly magenta-ish (full stamp: 255,0,255; preview over white: about 255,51,255).</summary>
    private static unsafe int CountMagenta(WriteableBitmap frame, int maxGreen)
    {
        using var fb = frame.Lock();
        var bgra = fb.Format == PixelFormat.Bgra8888;
        var count = 0;
        for (var y = 0; y < fb.Size.Height; y++)
        {
            var row = (byte*)fb.Address + y * fb.RowBytes;
            for (var x = 0; x < fb.Size.Width; x++)
            {
                var p = row + x * 4;
                var (r, b) = bgra ? (p[2], p[0]) : (p[0], p[2]);
                if (r >= 200 && b >= 200 && p[1] <= maxGreen && r - p[1] >= 40)
                    count++;
            }
        }
        return count;
    }

    private static async Task<(MainWindow Window, HorizontalPageView View, string Png)> OpenAsync(TempDir dir, int pages = 3)
    {
        var settings = new AppSettings { Language = "en", StampFolder = dir.Path };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", pages));
        await Pump();
        return (window, window.FindControl<HorizontalPageView>("PageView")!, MagentaPng(dir));
    }

    /// <summary>A window point inside the given page (fraction of its width), at half the height of the view.</summary>
    private static Point InPage(MainWindow window, HorizontalPageView view, int page, double fractionX = 0.5)
    {
        var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
        var y = view.Bounds.Height / 2;
        double? left = null, right = null;
        for (double x = 0; x < view.Bounds.Width; x += 1)
        {
            var inside = view.PageIndexAt(new Point(x, y)) == page;
            if (inside && left == null)
                left = x;
            if (!inside && left != null && right == null)
                right = x;
        }
        Assert.NotNull(left);
        return origin + new Point(left.Value + ((right ?? view.Bounds.Width) - left.Value) * fractionX, y);
    }

    [AvaloniaFact]
    public async Task ThePictureReplacesThePointerOverAPage()
    {
        using var dir = new TempDir();
        var (window, view, png) = await OpenAsync(dir);
        var vm = window.ViewModel;
        var surface = view.Children[0];
        var p = InPage(window, view, 0);

        vm.SelectStampCommand.Execute(png);
        await Pump(100);
        Assert.Equal("Cross", surface.Cursor?.ToString()); // the pointer has not moved over a page yet
        window.MouseMove(p);
        await Pump(100);
        Assert.Equal("None", surface.Cursor?.ToString());

        // Beside the pages there is no picture to show: the pointer comes back
        var b = view.PageRectInViewport(0);
        window.MouseMove(view.TranslatePoint(new Point(b.X + b.Width / 2, b.Y - 2), window)!.Value);
        await Pump(100);
        Assert.Equal("Cross", surface.Cursor?.ToString());
        Assert.Equal(0, CountMagenta(window.CaptureRenderedFrame()!, 255));

        // A swipe shows its own pointer and places nothing
        window.MouseMove(p);
        window.MouseDown(p, MouseButton.Left);
        window.MouseMove(p + new Point(10, 0));
        window.MouseMove(p + new Point(30, 0));
        window.MouseMove(p + new Point(40, 0));
        await Pump(100);
        Assert.Equal("SizeAll", surface.Cursor?.ToString());
        window.MouseUp(p + new Point(40, 0), MouseButton.Left);
        await Pump();
        Assert.Empty(vm.Document!.Pages[0].Stamps);
        window.MouseMove(InPage(window, view, 0));
        await Pump(100);
        Assert.Equal("None", surface.Cursor?.ToString());

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump(100);
        Assert.Null(vm.PendingStampPath);
        Assert.Null(surface.Cursor);
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task ChoosingAStampShowsAPreviewAndAClickPlacesItAsOneUndoStep()
    {
        using var dir = new TempDir();
        var (window, view, png) = await OpenAsync(dir);
        var vm = window.ViewModel;
        var p = InPage(window, view, 0);

        vm.SelectStampCommand.Execute(png);
        await Pump();
        Assert.NotNull(view.PendingStampImage);
        Assert.Equal(0, CountMagenta(window.CaptureRenderedFrame()!, 255)); // nothing yet: the pointer has not moved over a page

        window.MouseMove(p);
        await Pump();
        var preview = CountMagenta(window.CaptureRenderedFrame()!, 80);
        Assert.True(preview > 50, $"preview pixels: {preview}");
        Assert.Equal(0, CountMagenta(window.CaptureRenderedFrame()!, 40)); // slightly see-through, the page shows under it

        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();
        var stamp = Assert.Single(vm.Document!.Pages[0].Stamps);
        Assert.Empty(vm.Document.Pages[1].Stamps);
        Assert.Null(vm.PendingStampPath);
        Assert.Null(view.PendingStampImage);
        Assert.Equal(60, stamp.PixelWidth);
        Assert.Equal(File.ReadAllBytes(png), stamp.Image);
        Assert.Equal(45, stamp.Rect.Width, 3); // 60 px at 96 dpi = 45 pt
        Assert.Equal(22.5, stamp.Rect.Height, 3);
        Assert.True(vm.IsDirty);
        Assert.True(CountMagenta(window.CaptureRenderedFrame()!, 40) > 50); // drawn in full colour on the page

        vm.UndoCommand.Execute(null);
        await Pump();
        Assert.Empty(vm.Document.Pages[0].Stamps);
        Assert.Equal(0, CountMagenta(window.CaptureRenderedFrame()!, 255));
        vm.RedoCommand.Execute(null);
        await Pump();
        Assert.True(CountMagenta(window.CaptureRenderedFrame()!, 40) > 50);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ANumberStampIsPlacedInTheSizeItsResolutionGives()
    {
        using var dir = new TempDir();
        var number = dir.File("07.png");
        File.Copy(Path.Combine(EasyPDF.Core.Stamps.StampLibrary.SampleFolder, "Numbers", "07.png"), number);
        var settings = new AppSettings { Language = "en", StampFolder = dir.Path };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2, widthBase: 300)); // 400 x 200 pt: room for the stamp's own size
        await Pump();
        var view = window.FindControl<HorizontalPageView>("PageView")!;
        var p = InPage(window, view, 0);

        window.ViewModel.SelectStampCommand.Execute(number);
        await Pump();
        window.MouseMove(p);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();
        var stamp = Assert.Single(window.ViewModel.Document!.Pages[0].Stamps);
        Assert.Equal(236, stamp.PixelWidth);                 // all pixels kept: sharp in print
        Assert.Equal(56.64, stamp.Rect.Width, 2);            // 236 px at 300 dpi = 20 mm, not 177 pt as at 96 dpi
        Assert.Equal(56.64, stamp.Rect.Height, 2);
        window.Close();
        await window.ViewModel.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task EscapeAndRightClickCancelPlacingWithoutChangingTheDocument()
    {
        using var dir = new TempDir();
        var (window, view, png) = await OpenAsync(dir);
        var vm = window.ViewModel;
        var p = InPage(window, view, 0);

        vm.SelectStampCommand.Execute(png);
        await Pump();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        await Pump();
        Assert.Null(vm.PendingStampPath);
        Assert.Null(view.PendingStampImage);

        vm.SelectStampCommand.Execute(png);
        await Pump();
        window.MouseDown(p, MouseButton.Right);
        window.MouseUp(p, MouseButton.Right);
        await Pump();
        Assert.Null(view.PendingStampImage);
        Assert.False(vm.Document!.HasStamps);
        Assert.False(vm.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClickingOutsideAnyPageDoesNotPlaceAndKeepsPlacing()
    {
        using var dir = new TempDir();
        var (window, view, png) = await OpenAsync(dir);
        var vm = window.ViewModel;
        vm.SelectStampCommand.Execute(png);
        await Pump();
        var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
        var outside = origin + new Point(2, 2); // the margin around the first page
        Assert.Equal(-1, view.PageIndexAt(new Point(2, 2)));
        window.MouseDown(outside, MouseButton.Left);
        window.MouseUp(outside, MouseButton.Left);
        await Pump();
        Assert.False(vm.Document!.HasStamps);
        Assert.NotNull(view.PendingStampImage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task OnARotatedPageTheStampStaysUprightInTheViewAndKeepsItsPlaceWhenTurnedAgain()
    {
        using var dir = new TempDir();
        var (window, view, png) = await OpenAsync(dir, 2);
        var vm = window.ViewModel;
        vm.Document!.Rotate([0], 90);
        await Pump();
        var p = InPage(window, view, 0, 0.5);

        vm.SelectStampCommand.Execute(png);
        window.MouseMove(p);
        await Pump();
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();
        var page = vm.Document.Pages[0];
        var stamp = Assert.Single(page.Stamps);
        Assert.Equal(270, stamp.Rotation); // turned back by the page rotation so it looks upright
        var shown = PageGeometry.Rotate(stamp.Rect, page.BaseSize, page.ExtraRotation);
        Assert.Equal(45, shown.Width, 3);  // as seen: wider than tall, like the picture
        Assert.Equal(22.5, shown.Height, 3);
        Assert.True(CountMagenta(window.CaptureRenderedFrame()!, 40) > 50);

        // Turning the page back to normal: the stamp lies on its side now, in the same place on the page
        vm.Document.Rotate([0], 270);
        await Pump();
        Assert.Equal(stamp.Rect, vm.Document.Pages[0].Stamps[0].Rect);
        Assert.True(CountMagenta(window.CaptureRenderedFrame()!, 40) > 50);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ChoosingAStampInOrganizeModeSwitchesToReading()
    {
        using var dir = new TempDir();
        var (window, view, png) = await OpenAsync(dir);
        var vm = window.ViewModel;
        vm.ShowOrganizeCommand.Execute(null);
        await Pump();
        vm.SelectStampCommand.Execute(png);
        await Pump();
        Assert.True(vm.IsReadingMode);
        Assert.NotNull(view.PendingStampImage);
        window.Close();
    }
}
