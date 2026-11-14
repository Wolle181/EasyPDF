using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace EasyPDF.App.Tests;

public class SelectionColourTests
{
    /// <summary>Shows the control with all its text marked; counts the pixels of the selection colour and the white ones where the text is.</summary>
    private static async Task<(int Marked, int White)> Render(Control content, string theme, Func<Window, Rect> textArea)
    {
        var window = new Window
        {
            Width = 500,
            Height = 120,
            RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light,
            Content = content,
        };
        window.Show();
        if (content is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
        else if (content is SelectableTextBlock block)
        {
            block.SelectAll();
        }
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.True(window.TryFindResource("Selection.Background", window.ActualThemeVariant, out var brush));
        var selection = ((ISolidColorBrush)brush!).Color;
        var area = textArea(window);
        var frame = window.CaptureRenderedFrame()!;
        using var fb = frame.Lock();
        var row = new byte[fb.RowBytes];
        int marked = 0, white = 0;
        for (var y = (int)area.Top + 1; y < (int)area.Bottom - 1; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
            for (var x = (int)area.Left + 1; x < (int)area.Right - 1; x++)
            {
                // byte order is RGBA or BGRA depending on the platform
                var (r, g, b) = fb.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? (row[x * 4 + 2], row[x * 4 + 1], row[x * 4]) : (row[x * 4], row[x * 4 + 1], row[x * 4 + 2]);
                if (r == selection.R && g == selection.G && b == selection.B)
                    marked++;
                else if (r > 240 && g > 240 && b > 240)
                    white++;
            }
        }
        window.Close();
        return (marked, white);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task MarkedTextInATextBoxIsWhiteOnTheSelectionColour(string theme)
    {
        var box = new TextBox { Text = "MMMMMMMMMM", FontSize = 28, Width = 400 };
        var (marked, white) = await Render(box, theme, window =>
        {
            var presenter = box.GetVisualDescendants().OfType<TextPresenter>().First();
            var origin = presenter.TranslatePoint(new Point(0, 0), window)!.Value;
            return new Rect(origin, new Size(presenter.TextLayout.Width, presenter.Bounds.Height));
        });
        Assert.True(marked > 500, $"only {marked} pixels in the selection colour");
        Assert.True(white > 100, $"only {white} white pixels: the marked text is not white");
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task MarkedTextInASelectableTextBlockIsWhiteOnTheSelectionColour(string theme)
    {
        var block = new SelectableTextBlock { Text = "MMMMMMMMMM", FontSize = 28, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var (marked, white) = await Render(block, theme, window => new Rect(block.TranslatePoint(new Point(0, 0), window)!.Value, block.Bounds.Size));
        Assert.True(marked > 500, $"only {marked} pixels in the selection colour");
        Assert.True(white > 100, $"only {white} white pixels: the marked text is not white");
    }
}
