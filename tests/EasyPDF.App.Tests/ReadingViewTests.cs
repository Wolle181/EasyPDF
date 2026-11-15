using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using EasyPDF.App.Views;
using Xunit;

namespace EasyPDF.App.Tests;

public class ReadingViewTests
{
    private static string DemoPdf()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "EasyPDF.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "demo-pdfs", "Annual Report 2025.pdf");
    }

    private static async Task Pump(int ms = 300)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static async Task<MainWindow> OpenWindowAsync()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(DemoPdf());
        await Pump();
        return window;
    }

    [AvaloniaFact]
    public async Task OpensDemoPdfAndRendersPages()
    {
        var window = await OpenWindowAsync();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        window.Close();
    }

    private static (EasyPDF.App.Controls.HorizontalPageView View, Point Center) ReadingView(MainWindow window)
    {
        var view = window.FindControl<EasyPDF.App.Controls.HorizontalPageView>("PageView")!;
        var center = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
        return (view, center);
    }

    [AvaloniaFact]
    public async Task CtrlGOpensTheGoToPageBoxAndEnterJumpsThere()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 40));
        await Pump();
        var vm = window.ViewModel;
        var button = window.FindControl<Button>("StatusPageButton")!;
        var flyout = Assert.IsType<Flyout>(button.Flyout);

        window.KeyPress(Key.G, RawInputModifiers.Control, PhysicalKey.None, null);
        await Pump();
        Assert.True(flyout.IsOpen);
        var box = Assert.IsType<TextBox>(((StackPanel)flyout.Content!).Children[1]);
        Assert.Equal("1", box.Text); // starts with the current page

        box.Text = "25";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Pump();
        Assert.Equal(24, vm.CurrentPage);
        Assert.False(flyout.IsOpen);

        // Out-of-range numbers go to the last page, garbage is ignored
        vm.GoToTypedPageCommand.Execute("999");
        Assert.Equal(39, vm.CurrentPage);
        vm.GoToTypedPageCommand.Execute("abc");
        Assert.Equal(39, vm.CurrentPage);
        vm.GoToTypedPageCommand.Execute("0");
        Assert.Equal(0, vm.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlHomeAndCtrlEndJumpToTheFirstAndLastPageInBothModes()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 40));
        await Pump();
        var vm = window.ViewModel;
        var view = window.FindControl<EasyPDF.App.Controls.HorizontalPageView>("PageView")!;
        var grid = window.FindControl<EasyPDF.App.Controls.ThumbnailGrid>("OrganizeView")!;

        // Reading mode
        window.KeyPress(Key.End, RawInputModifiers.Control, PhysicalKey.None, null);
        await Pump();
        Assert.True(view.ScrollOffset.X > 1000);               // scrolled to the end ...
        Assert.True(view.PageIndexAt(new Point(view.Bounds.Width - 40, view.Bounds.Height / 2)) == 39); // ... the last page is visible
        window.KeyPress(Key.Home, RawInputModifiers.Control, PhysicalKey.None, null);
        await Pump();
        Assert.Equal(0, view.ScrollOffset.X);
        Assert.Equal(0, vm.CurrentPage);

        // Organize mode
        vm.ShowOrganizeCommand.Execute(null);
        await Pump();
        window.KeyPress(Key.End, RawInputModifiers.Control, PhysicalKey.None, null);
        await Pump();
        Assert.Equal(39, vm.CurrentPage);
        bool Shows(int page) =>                                // is the page anywhere in the visible part of the grid?
            Enumerable.Range(0, (int)grid.Bounds.Height / 10).Any(y =>
                Enumerable.Range(0, (int)grid.Bounds.Width / 20).Any(x => grid.PageIndexAt(new Point(x * 20, y * 10)) == page));
        Assert.False(Shows(0));
        Assert.True(Shows(39));                                // the last page is in view
        window.KeyPress(Key.Home, RawInputModifiers.Control, PhysicalKey.None, null);
        await Pump();
        Assert.Equal(0, vm.CurrentPage);
        Assert.True(Shows(0));
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task DraggingWithTheMouseSwipesTheContentWithoutMarkingAPage()
    {
        var window = await OpenWindowAsync();
        var (view, center) = ReadingView(window);
        var vm = window.ViewModel;
        var start = view.ScrollOffset.X;

        // Drag to the left: the content follows the pointer, so further pages come in from the right
        window.MouseDown(center, MouseButton.Left);
        for (var i = 1; i <= 4; i++)
        {
            window.MouseMove(center - new Vector(50 * i, 0));
            await Pump(40);
        }
        Assert.True(view.IsPanning);
        Assert.Equal(start + 200, view.ScrollOffset.X, 0);
        await Pump(200);                                       // rest before releasing: no gliding
        window.MouseUp(center - new Vector(200, 0), MouseButton.Left);
        Assert.False(view.IsPanning);
        Assert.False(view.IsFlinging);
        Assert.Equal(start + 200, view.ScrollOffset.X, 0);
        Assert.Equal(-1, vm.MarkedPageId);                     // a swipe is not a click: no red frame

        // Drag to the right: back towards the beginning
        window.MouseDown(center, MouseButton.Left);
        window.MouseMove(center + new Vector(120, 0));
        await Pump(200);
        window.MouseUp(center + new Vector(120, 0), MouseButton.Left);
        Assert.Equal(start + 80, view.ScrollOffset.X, 0);

        // A click without moving still marks the page
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Assert.NotEqual(-1, vm.MarkedPageId);
        window.Close();
    }

    /// <summary>
    /// Swipes 300 px to the left in five steps of <paramref name="stepMs"/> and returns the glide speed at release.
    /// The time comes from the view's clock, set here: real delays are longer than asked (timer resolution, a busy
    /// machine), and the speed is measured over the last 100 ms only, so the result varied from run to run.
    /// </summary>
    private static double Swipe(MainWindow window, EasyPDF.App.Controls.HorizontalPageView view, Point center, int stepMs)
    {
        long now = 0;
        view.Clock = () => now;
        window.MouseDown(center + new Vector(150, 0), MouseButton.Left);
        for (var i = 1; i <= 5; i++)
        {
            now += stepMs;
            window.MouseMove(center + new Vector(150 - 60 * i, 0));
        }
        window.MouseUp(center - new Vector(150, 0), MouseButton.Left);
        var speed = view.FlingSpeed;
        window.MouseDown(center, MouseButton.Left);            // stop it again
        window.MouseUp(center, MouseButton.Left);
        return speed;
    }

    [AvaloniaFact]
    public async Task AFasterSwipeGlidesWithMoreMomentum()
    {
        var window = await OpenWindowAsync();
        var (view, center) = ReadingView(window);
        var slow = Swipe(window, view, center, 40);  // 300 px in 200 ms
        var fast = Swipe(window, view, center, 5);   // 300 px in 25 ms
        // Measured over the last 100 ms before release: 120 px in 80 ms, and the whole 300 px in 25 ms
        Assert.Equal(1.5, slow, 3);
        Assert.Equal(12, fast, 3);
        Assert.True(slow > 0, "a moderate swipe glides on a little");
        Assert.True(fast > slow * 2, $"fast {fast:0.00} px/ms vs slow {slow:0.00} px/ms");
        window.Close();
    }

    [AvaloniaFact]
    public async Task AFastSwipeGlidesOnAndAClickStopsIt()
    {
        var window = await OpenWindowAsync();
        var (view, center) = ReadingView(window);
        var start = view.ScrollOffset.X;

        window.MouseDown(center + new Vector(200, 0), MouseButton.Left);
        for (var i = 1; i <= 5; i++)
        {
            window.MouseMove(center + new Vector(200 - 60 * i, 0)); // 300 px to the left, quickly
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(12, TestContext.Current.CancellationToken);
        }
        window.MouseUp(center - new Vector(100, 0), MouseButton.Left);
        Assert.True(view.IsFlinging);
        var released = view.ScrollOffset.X;
        Assert.True(released >= start + 299);                  // the swipe itself (the glide may have begun already)

        await Pump(300);
        Assert.True(view.ScrollOffset.X > released + 50);      // it kept moving in the swipe direction

        window.MouseDown(center, MouseButton.Left);            // a click catches it
        Assert.False(view.IsFlinging);
        var stopped = view.ScrollOffset.X;
        window.MouseUp(center, MouseButton.Left);
        await Pump(200);
        Assert.Equal(stopped, view.ScrollOffset.X, 0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task HorizontalWheelScrollingDoesNotThrow()
    {
        var window = await OpenWindowAsync();
        var center = new Point(500, 350);
        for (var i = 0; i < 40; i++)
        {
            window.MouseWheel(center, new Vector(0, -1));
            await Pump(40);
        }
        for (var i = 0; i < 40; i++)
        {
            window.MouseWheel(center, new Vector(0, 1));
            await Pump(40);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task KeyboardNavigationAndZoomDoNotThrow()
    {
        var window = await OpenWindowAsync();
        window.MouseWheel(new Point(500, 350), new Vector(0, -1));
        foreach (var key in new[] { Key.Right, Key.Right, Key.PageDown, Key.PageDown, Key.PageUp, Key.End, Key.Home, Key.Left })
        {
            window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            await Pump(60);
        }
        window.MouseWheel(new Point(500, 350), new Vector(0, 3), RawInputModifiers.Control);
        await Pump();
        window.KeyPress(Key.OemPlus, RawInputModifiers.Control, PhysicalKey.None, null);
        await Pump();
        window.Close();
    }

    private static async Task DropFileAsync(MainWindow window, string path)
    {
        var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        Assert.NotNull(file);
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(file));
        var point = new Point(400, 300);
        window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        await Pump(1500);
    }

    [AvaloniaFact]
    public async Task DroppingAPdfOpensItAndDroppingAnotherReplacesIt()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await DropFileAsync(window, DemoPdf());
        Assert.StartsWith("Annual Report 2025", window.Title);
        await DropFileAsync(window, DemoPdf());
        window.MouseWheel(new Point(500, 350), new Vector(0, -1));
        await Pump();
        window.Close();
    }
}
