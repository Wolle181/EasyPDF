using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Controls;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class TextPlacementTests
{
    private const string TwoLines = "Hello\nWorld";

    private static async Task Pump(int ms = 300)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static TextBox? Editor(HorizontalPageView view) => view.Children.OfType<TextBox>().FirstOrDefault();

    private static void Click(MainWindow window, Point p, MouseButton button = MouseButton.Left)
    {
        window.MouseDown(p, button);
        window.MouseUp(p, button);
    }

    private static void FinishEditing(MainWindow window) =>
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

    [AvaloniaFact]
    public async Task ClickTypeAndFinishPlacesTheTextAndDoubleClickEditsIt()
    {
        var settings = new AppSettings { Language = "en", TextColor = "#C00000", TextFont = "Times", TextFontSize = 14 };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        await Pump();
        var view = window.FindControl<HorizontalPageView>("PageView")!;
        var vm = window.ViewModel;

        var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
        var page = view.PageRectInViewport(0);
        Point At(double ptX, double ptY) => origin + new Point(page.X + ptX * page.Width / 100, page.Y + ptY * page.Width / 100);

        // Without the tool a click places nothing
        Click(window, At(20, 40));
        await Pump();
        Assert.Null(Editor(view));

        vm.SelectTextToolCommand.Execute(null);
        await Pump();
        Click(window, At(20, 40));
        await Pump();
        var editor = Editor(view);
        Assert.NotNull(editor);
        Assert.True(editor!.IsFocused, "focus: " + (TopLevel.GetTopLevel(editor)?.FocusManager?.GetFocusedElement()?.ToString() ?? "none"));
        editor.Text = TwoLines;
        FinishEditing(window);
        await Pump();

        Assert.Null(Editor(view));
        Assert.False(vm.IsTextToolActive);
        var text = Assert.Single(vm.Document!.Pages[0].Texts);
        Assert.Equal(TwoLines, text.Text);
        Assert.Equal("Times", text.Font);
        Assert.Equal(14, text.FontSize);
        Assert.Equal("#C00000", text.Color);
        Assert.Equal(20, text.Rect.X, 0);
        Assert.Equal(40, text.Rect.Y, 0);
        Assert.True(text.Rect.Width > 10 && text.Rect.Height > 20, text.Rect.ToString());

        // Drawn: dark red pixels appear where the text is
        Assert.True(CountRed(window.CaptureRenderedFrame()!) > 20);

        // Double click edits it again; changing the text is one more undo step
        var inside = At(text.Rect.X + 3, text.Rect.Y + 3);
        Click(window, inside);
        Click(window, inside);
        await Pump();
        editor = Editor(view);
        Assert.NotNull(editor);
        Assert.Equal(TwoLines, editor!.Text);
        editor.Text = "Changed";
        FinishEditing(window);
        await Pump();
        Assert.Equal("Changed", vm.Document.Pages[0].Texts.Single().Text);
        vm.Document.Undo();
        Assert.Equal(TwoLines, vm.Document.Pages[0].Texts.Single().Text);
        vm.Document.Redo();

        // Emptying the text removes it
        inside = At(text.Rect.X + 3, text.Rect.Y + 3);
        Click(window, inside);
        Click(window, inside);
        await Pump();
        Editor(view)!.Text = "";
        FinishEditing(window);
        await Pump();
        Assert.Empty(vm.Document.Pages[0].Texts);

        window.Close();
        await vm.Document.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task AnEmptyNewTextPlacesNothingAndRightClickEndsTheTool()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        var view = window.FindControl<HorizontalPageView>("PageView")!;
        var vm = window.ViewModel;
        var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
        var page = view.PageRectInViewport(0);
        var p = origin + new Point(page.X + page.Width / 2, page.Y + page.Height / 2);

        vm.SelectTextToolCommand.Execute(null);
        Click(window, p);
        await Pump();
        Assert.NotNull(Editor(view));
        FinishEditing(window);
        await Pump();
        Assert.Empty(vm.Document!.Pages[0].Texts);
        Assert.False(vm.Document.IsDirty);

        vm.SelectTextToolCommand.Execute(null);
        Click(window, p, MouseButton.Right);
        await Pump();
        Assert.False(vm.IsTextToolActive);
        Assert.Null(Editor(view));

        window.Close();
        await vm.Document.DisposeAsync();
    }

    private static unsafe int CountRed(Avalonia.Media.Imaging.WriteableBitmap frame)
    {
        using var fb = frame.Lock();
        var bgra = fb.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        var count = 0;
        for (var y = 0; y < fb.Size.Height; y++)
        {
            var row = (byte*)fb.Address + y * fb.RowBytes;
            for (var x = 0; x < fb.Size.Width; x++)
            {
                var p = row + x * 4;
                var (r, b) = bgra ? (p[2], p[0]) : (p[0], p[2]);
                if (r >= 150 && r <= 230 && p[1] < 60 && b < 60)
                    count++;
            }
        }
        return count;
    }
}
