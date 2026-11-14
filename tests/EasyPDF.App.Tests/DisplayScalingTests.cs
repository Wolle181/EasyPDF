using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EasyPDF.App.Controls;
using EasyPDF.App.Views;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>
/// Pages are rendered with the display scaling (more pixels than DIPs). Avalonia reads DrawImage's source rectangle in pixels,
/// so a source rectangle in DIPs showed only the top-left part of each page at 125 % / 150 % (the bottom-right corner was missing).
/// </summary>
public class DisplayScalingTests
{
    private static async Task Pump(int ms = 600)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    /// <summary>Pure blue pixels of the frame inside <paramref name="control"/> (the blue corner block of the test page).</summary>
    private static unsafe int CountBluePixels(Window window, Control control, double scaling)
    {
        var topLeft = control.TranslatePoint(default, window)!.Value;
        var frame = window.CaptureRenderedFrame()!;
        using var fb = frame.Lock();
        var bgra = fb.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        int x0 = (int)(topLeft.X * scaling), y0 = (int)(topLeft.Y * scaling);
        int x1 = Math.Min(fb.Size.Width, (int)((topLeft.X + control.Bounds.Width) * scaling));
        int y1 = Math.Min(fb.Size.Height, (int)((topLeft.Y + control.Bounds.Height) * scaling));
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = (byte*)fb.Address + y * fb.RowBytes;
            for (var x = x0; x < x1; x++)
            {
                var p = row + x * 4;
                var (r, b) = bgra ? (p[2], p[0]) : (p[0], p[2]);
                if (b >= 200 && r <= 60 && p[1] <= 60)
                    count++;
            }
        }
        return count;
    }

    [AvaloniaTheory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task WholePageIsShownAtDisplayScaling(double scaling)
    {
        using var dir = new TempDir();
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        window.SetRenderScaling(scaling);
        await window.OpenPathAsync(dir.CornerPdf("corner.pdf"));
        await Pump();

        Assert.True(CountBluePixels(window, window.FindControl<HorizontalPageView>("PageView")!, scaling) > 100);
        Assert.True(CountBluePixels(window, window.FindControl<ThumbnailGrid>("SidebarView")!, scaling) > 10);

        window.ViewModel.ShowOrganizeCommand.Execute(null);
        await Pump();
        Assert.True(CountBluePixels(window, window.FindControl<ThumbnailGrid>("OrganizeView")!, scaling) > 100);
        window.Close();
    }
}
