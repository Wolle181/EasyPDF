using Avalonia;
using Avalonia.Controls;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;

namespace EasyPDF.App.Tests;

public class StampEditTests
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
    public async Task ASelectedStampCanBeMovedResizedAndDeletedWithUndo()
    {
        using var dir = new TempDir();
        var settings = new EasyPDF.Core.Settings.AppSettings { Language = "en", StampFolder = dir.Path };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new EasyPDF.App.Views.MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        await Pump();
        var view = window.FindControl<EasyPDF.App.Controls.HorizontalPageView>("PageView")!;
        var vm = window.ViewModel;

        // Place a stamp via the view model (placement itself is covered elsewhere)
        var page = vm.Document!.Pages[0];
        var start = new EasyPDF.Core.Documents.RectPt(10, 20, 40, 20);
        var stamp = vm.Document.AddStamp(0, [0], 4, 2, start);
        await Pump();

        var origin = view.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
        var b = view.PageRectInViewport(0);
        var ppp = b.Width / page.DisplaySize.Width;
        Avalonia.Point At(double x, double y) => origin + new Avalonia.Point(b.X + x * ppp, b.Y + y * ppp);

        // Click selects, drag moves by 50pt right / 30pt down
        var from = At(30, 30);
        var to = At(50, 60);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        await Pump();
        Assert.Equal(stamp.Id, view.SelectedStampId);
        var moved = vm.Document.Pages[0].Stamps.Single().Rect;
        Assert.Equal(30, moved.X, 0);
        Assert.Equal(50, moved.Y, 0);
        Assert.Equal(40, moved.Width, 3);

        // Dragging the bottom-right corner enlarges it, keeping the aspect ratio
        var corner = At(moved.Right, moved.Bottom);
        var cornerTo = At(moved.Right + 20, moved.Bottom + 2);
        window.MouseDown(corner, MouseButton.Left);
        window.MouseMove(cornerTo);
        window.MouseUp(cornerTo, MouseButton.Left);
        await Pump();
        var resized = vm.Document.Pages[0].Stamps.Single().Rect;
        Assert.Equal(30, resized.X, 0);
        Assert.Equal(60, resized.Width, 0);
        Assert.Equal(30, resized.Height, 0);

        // Undo steps back through resize and move
        vm.Document.Undo();
        Assert.Equal(40, vm.Document.Pages[0].Stamps.Single().Rect.Width, 3);
        vm.Document.Undo();
        Assert.Equal(10, vm.Document.Pages[0].Stamps.Single().Rect.X, 3);
        vm.Document.Redo();
        vm.Document.Redo();

        // Delete removes the selected stamp; undo brings it back
        view.Focus();
        Assert.True(view.IsKeyboardFocusWithin, "focus: " + (TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement()?.ToString() ?? "none"));
        Assert.Equal(stamp.Id, view.SelectedStampId);
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        await Pump();
        Assert.Empty(vm.Document.Pages[0].Stamps);
        Assert.Equal(3, vm.Document.PageCount);
        vm.Document.Undo();
        Assert.Single(vm.Document.Pages[0].Stamps);
    }
}
