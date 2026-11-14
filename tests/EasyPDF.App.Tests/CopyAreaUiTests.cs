using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Controls;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Copy area and paste area in the reading view: frame a part of a page, place the copy elsewhere, opaque.</summary>
public class CopyAreaUiTests
{
    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 150 && !done(); i++)
            await Pump(20);
        Assert.True(done(), "timed out");
    }

    /// <summary>A point of page <paramref name="index"/> (page points, as shown) in window coordinates.</summary>
    private static Point At(MainWindow window, HorizontalPageView view, int index, double x, double y)
    {
        var page = view.PageRectInViewport(index);
        var scale = page.Width / window.ViewModel.Document!.Pages[index].DisplaySize.Width;
        return view.TranslatePoint(new Point(page.X + x * scale, page.Y + y * scale), window)!.Value;
    }

    private static void Drag(MainWindow window, HorizontalPageView view, int index, (double X, double Y) from, (double X, double Y) to)
    {
        window.MouseDown(At(window, view, index, from.X, from.Y), MouseButton.Left);
        window.MouseMove(At(window, view, index, (from.X + to.X) / 2, (from.Y + to.Y) / 2));
        window.MouseMove(At(window, view, index, to.X, to.Y));
        window.MouseUp(At(window, view, index, to.X, to.Y), MouseButton.Left);
    }

    private static void Click(MainWindow window, HorizontalPageView view, int index, double x, double y)
    {
        window.MouseDown(At(window, view, index, x, y), MouseButton.Left);
        window.MouseUp(At(window, view, index, x, y), MouseButton.Left);
    }

    private static void AssertNear(RectPt expected, RectPt actual, double tolerance = 1.5)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance
            && Math.Abs(expected.Width - actual.Width) <= tolerance && Math.Abs(expected.Height - actual.Height) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    /// <summary>Grey value of a pixel of the copied picture at a point of the page (points, as shown).</summary>
    private static int GreyAt(CopiedArea area, RectPt copied, double x, double y)
    {
        var px = (int)((x - copied.X) / copied.Width * area.PixelWidth);
        var py = (int)((y - copied.Y) / copied.Height * area.PixelHeight);
        var i = (py * area.PixelWidth + px) * 4;
        return (area.Bgra[i] + area.Bgra[i + 1] + area.Bgra[i + 2]) / 3;
    }

    [AvaloniaFact]
    public async Task AFramedAreaIsCopiedWithItsBoxesAndPlacedOpaqueOnAnotherPage()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        var view = window.FindControl<HorizontalPageView>("PageView")!;
        var vm = window.ViewModel;
        var document = vm.Document!;
        document.AddRedactions([(0, new RectPt(20, 20, 40, 40))], RedactionFill.Black);
        await Pump();

        var button = window.FindControl<RibbonButton>("CopyAreaButton")!;
        Assert.Same(vm.ToggleCopyAreaToolCommand, button.Command);
        Assert.False(vm.PasteAreaCommand.CanExecute(null)); // nothing copied yet
        button.Command!.Execute(null);
        await Pump();
        Assert.True(vm.IsCopyAreaToolActive);
        Assert.True(view.IsCopyAreaToolActive);
        Assert.True(button.IsActive);
        Assert.Equal(Loc.T("CopyAreaToolSelected"), vm.StatusMessage);

        // A frame around the box copies it; the content does not move and nothing is added to the page
        var scroll = view.ScrollOffset;
        Drag(window, view, 0, (10, 10), (70, 70));
        await WaitUntil(() => vm.PendingPaste != null);
        Assert.Equal(scroll, view.ScrollOffset);
        var area = vm.AreaClipboard.Current!;
        Assert.Same(area, vm.PendingPaste);
        Assert.False(vm.IsCopyAreaToolActive);
        Assert.Equal(Loc.T("MsgAreaCopied"), vm.StatusMessage);
        Assert.Empty(document.Pages[0].Stamps);
        Assert.InRange(area.WidthPt, 59, 61.5);
        Assert.InRange(area.HeightPt, 59, 61.5);
        Assert.InRange(area.PixelWidth, 245, 260); // 300 dpi
        var copied = new RectPt(10, 10, area.WidthPt, area.HeightPt);
        Assert.Equal(0, GreyAt(area, copied, 40, 40));   // the black box placed on the page is in the copy
        Assert.Equal(255, GreyAt(area, copied, 13, 13)); // the empty page around it
        Assert.All(Enumerable.Range(0, area.PixelWidth * area.PixelHeight), i => Assert.Equal(255, area.Bgra[i * 4 + 3])); // opaque
        Assert.NotNull(view.PendingStampImage);

        // A click on the second page places it there in the size it was copied
        Click(window, view, 1, 50, 100);
        await Pump();
        var pasted = Assert.Single(document.Pages[1].Stamps);
        Assert.True(pasted.CoversContent);
        AssertNear(new RectPt(50 - area.WidthPt / 2, 100 - area.HeightPt / 2, area.WidthPt, area.HeightPt), pasted.Rect);
        Assert.Null(vm.PendingPaste);
        Assert.Null(view.PendingStampImage);
        Assert.Equal(Loc.T("MsgAreaPasted"), vm.StatusMessage);
        Assert.True(document.RemovesContent);

        // Paste again; Esc cancels pasting but keeps the copy
        Assert.True(vm.PasteAreaCommand.CanExecute(null));
        vm.PasteAreaCommand.Execute(null);
        await Pump();
        Assert.NotNull(view.PendingStampImage);
        Assert.Equal(Loc.T("PasteAreaSelected"), vm.StatusMessage);
        window.MouseMove(At(window, view, 1, 50, 100));
        await Pump();
        Assert.Equal("None", view.Children[0].Cursor?.ToString()); // the copy takes the place of the pointer, like a stamp
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump();
        Assert.Null(vm.PendingPaste);
        Assert.Null(view.PendingStampImage);
        Assert.Null(view.Children[0].Cursor);
        Assert.Same(area, vm.AreaClipboard.Current);
        Assert.Single(document.Pages[1].Stamps);

        // The pasted area is moved like a stamp; Undo takes it back step by step
        await Pump(600);
        Drag(window, view, 1, (50, 100), (60, 120));
        await Pump();
        AssertNear(new RectPt(60 - area.WidthPt / 2, 120 - area.HeightPt / 2, area.WidthPt, area.HeightPt), document.Pages[1].Stamps[0].Rect);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        await Pump();
        Assert.Empty(document.Pages[1].Stamps);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CtrlVPastesAnAreaCopiedInAnotherWindow()
    {
        using var dir = new TempDir();
        var windows = new WindowManager(new AppSettings());
        var first = windows.CreateWindow();
        first.Width = 1000;
        first.Height = 700;
        first.Show();
        await windows.OpenFilesAsync([dir.Pdf("a.pdf", 1)], merge: false);
        await windows.OpenFilesAsync([dir.Pdf("b.pdf", 1)], merge: false);
        await Pump();
        var second = windows.Windows.Single(w => w != first);
        Assert.Same(first.ViewModel.AreaClipboard, second.ViewModel.AreaClipboard);

        var view = first.FindControl<HorizontalPageView>("PageView")!;
        first.ViewModel.ToggleCopyAreaToolCommand.Execute(null);
        await Pump();
        Drag(first, view, 0, (10, 10), (50, 40));
        await WaitUntil(() => first.ViewModel.PendingPaste != null);
        first.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump();
        Assert.Null(first.ViewModel.PendingPaste);

        second.Activate();
        await Pump();
        var otherView = second.FindControl<HorizontalPageView>("PageView")!;
        otherView.Focus();
        second.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        await Pump();
        Assert.Same(first.ViewModel.AreaClipboard.Current, second.ViewModel.PendingPaste);
        Click(second, otherView, 0, 50, 100);
        await Pump();
        var pasted = Assert.Single(second.ViewModel.Document!.Pages[0].Stamps);
        Assert.True(pasted.CoversContent);
        Assert.Empty(first.ViewModel.Document!.Pages[0].Stamps);

        var documents = windows.Windows.Select(w => w.ViewModel.Document).OfType<EditableDocument>().ToList();
        foreach (var window in windows.Windows.ToList())
            window.CloseWindow();
        foreach (var document in documents)
            await document.DisposeAsync();
    }
}
