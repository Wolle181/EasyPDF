using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Controls;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class TextEditTests
{
    private static async Task Pump(int ms = 300)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task ASelectedTextCanBeMovedScaledAndDeletedWithUndo()
    {
        using var dir = new TempDir();
        var settings = new AppSettings { Language = "en" };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new EasyPDF.App.Views.MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        await Pump();
        var view = window.FindControl<HorizontalPageView>("PageView")!;
        var vm = window.ViewModel;

        var page = vm.Document!.Pages[0];
        var text = vm.Document.AddText(0, "Hello", "Helvetica", 12, "#000000", new RectPt(10, 20, 40, 20));
        await Pump();

        var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
        var b = view.PageRectInViewport(0);
        var ppp = b.Width / page.DisplaySize.Width;
        Point At(double x, double y) => origin + new Point(b.X + x * ppp, b.Y + y * ppp);

        // Click selects, drag moves by 20pt right / 30pt down
        var from = At(30, 30);
        var to = At(50, 60);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        await Pump();
        Assert.Equal(text.Id, view.SelectedStampId);
        var moved = vm.Document.Pages[0].Texts.Single();
        Assert.Equal(30, moved.Rect.X, 0);
        Assert.Equal(50, moved.Rect.Y, 0);
        Assert.Equal(12, moved.FontSize, 3);

        // Dragging the bottom-right corner grows the width by half: the font size grows by half, the text stays the same
        var corner = At(moved.Rect.Right, moved.Rect.Bottom);
        var cornerTo = At(moved.Rect.Right + 20, moved.Rect.Bottom + 1);
        window.MouseDown(corner, MouseButton.Left);
        window.MouseMove(cornerTo);
        window.MouseUp(cornerTo, MouseButton.Left);
        await Pump();
        var scaled = vm.Document.Pages[0].Texts.Single();
        Assert.Equal(60, scaled.Rect.Width, 0);
        Assert.Equal(18, scaled.FontSize, 0);
        Assert.Equal("Hello", scaled.Text);

        vm.Document.Undo();
        Assert.Equal(12, vm.Document.Pages[0].Texts.Single().FontSize, 3);
        vm.Document.Undo();
        Assert.Equal(10, vm.Document.Pages[0].Texts.Single().Rect.X, 3);
        vm.Document.Redo();
        vm.Document.Redo();

        // Delete removes the selected text; undo brings it back
        view.Focus();
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        await Pump();
        Assert.Empty(vm.Document.Pages[0].Texts);
        Assert.Equal(3, vm.Document.PageCount);
        vm.Document.Undo();
        Assert.Single(vm.Document.Pages[0].Texts);

        window.Close();
        await vm.ReleaseDocumentAsync();
    }
}
