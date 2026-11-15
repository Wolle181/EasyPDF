using System;
using System.Collections.Generic;
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
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Black out and white out in the reading view: boxes around areas, over dragged text, over selected text.</summary>
public class RedactionUiTests
{
    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
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

    private static void AssertNear(RectPt expected, RectPt actual, double tolerance = 1.5)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance
            && Math.Abs(expected.Width - actual.Width) <= tolerance && Math.Abs(expected.Height - actual.Height) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    /// <summary>Two pages of 100 × 200 pt; the first with the recognized words "Hallo schöne Welt" and "Zeile" below.</summary>
    private static async Task<(MainWindow Window, HorizontalPageView View, TempDir Dir)> OpenAsync()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        var document = window.ViewModel.Document!;
        document.SetOcrWords(new Dictionary<int, IReadOnlyList<OcrWord>>
        {
            [document.Pages[0].Id] =
            [
                new OcrWord("Hallo", new RectPt(10, 20, 25, 10), 1),
                new OcrWord("schöne", new RectPt(40, 20, 30, 10), 1),
                new OcrWord("Welt", new RectPt(75, 20, 20, 10), 1),
                new OcrWord("Zeile", new RectPt(10, 40, 25, 10), 1),
            ],
        });
        await Pump();
        return (window, window.FindControl<HorizontalPageView>("PageView")!, dir);
    }

    [AvaloniaFact]
    public async Task TheBlackOutToolDrawsBoxesUntilEscAndABoxCanBeMovedAndDeleted()
    {
        var (window, view, dir) = await OpenAsync();
        using var _ = dir;
        var vm = window.ViewModel;
        var button = window.FindControl<RibbonButton>("RedactButton")!;
        Assert.Same(vm.RedactCommand, button.Command);
        button.Command!.Execute(null);
        await Pump();
        Assert.True(vm.IsRedactToolActive);
        Assert.Equal(RedactionFill.Black, view.ActiveRedaction);
        Assert.True(button.IsActive);
        Assert.Equal(Loc.T("RedactToolSelected"), vm.StatusMessage);

        // A drag beside the text draws a box; the content does not move
        var scroll = view.ScrollOffset;
        Drag(window, view, 0, (20, 100), (80, 140));
        await Pump();
        Assert.Equal(scroll, view.ScrollOffset);
        var box = Assert.Single(vm.Document!.Pages[0].Redactions);
        Assert.Equal(RedactionFill.Black, box.Fill);
        AssertNear(new RectPt(20, 100, 60, 40), box.Rect);
        Assert.Equal(Loc.T("MsgRedacted"), vm.StatusMessage);
        Assert.True(vm.IsDirty);

        // The tool stays on; a plain click draws nothing; Ctrl+Z takes a box back
        Drag(window, view, 0, (80, 160), (20, 190)); // up and to the left works too
        await Pump();
        Assert.Equal(2, vm.Document.Pages[0].Redactions.Count);
        AssertNear(new RectPt(20, 160, 60, 30), vm.Document.Pages[0].Redactions[1].Rect);
        await Pump(600);
        window.MouseDown(At(window, view, 0, 50, 70), MouseButton.Left);
        window.MouseUp(At(window, view, 0, 50, 70), MouseButton.Left);
        await Pump();
        Assert.Equal(2, vm.Document.Pages[0].Redactions.Count);
        vm.UndoCommand.Execute(null);
        await Pump();
        Assert.Single(vm.Document.Pages[0].Redactions);

        // Esc ends the tool; then the box can be moved, and Del removes it
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump();
        Assert.Null(vm.ActiveRedaction);
        Assert.Null(view.ActiveRedaction);
        Assert.False(button.IsActive);
        Drag(window, view, 0, (50, 120), (60, 130));
        await Pump();
        AssertNear(new RectPt(30, 110, 60, 40), vm.Document.Pages[0].Redactions[0].Rect);
        Assert.Equal(vm.Document.Pages[0].Redactions[0].Id, view.SelectedStampId);
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        await Pump();
        Assert.Empty(vm.Document.Pages[0].Redactions);
        Assert.Equal(2, vm.Document.PageCount); // Del removed the box, not the page
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task DraggingOverTextWithTheWhiteOutToolCoversThoseWordsAndTheyCanNoLongerBeSelected()
    {
        var (window, view, dir) = await OpenAsync();
        using var _ = dir;
        var vm = window.ViewModel;
        window.FindControl<RibbonButton>("CoverButton")!.Command!.Execute(null);
        await Pump();
        Assert.True(vm.IsCoverToolActive);
        Assert.Equal(Loc.T("CoverToolSelected"), vm.StatusMessage);

        // From "Hallo" into "schöne": one band over both words (a little larger than them)
        Drag(window, view, 0, (12, 25), (55, 25));
        await Pump();
        var box = Assert.Single(vm.Document!.Pages[0].Redactions);
        Assert.Equal(RedactionFill.White, box.Fill);
        AssertNear(new RectPt(9, 18.5, 62, 13), box.Rect);
        Assert.Null(vm.SelectedText);
        Assert.Equal(Loc.T("MsgCovered"), vm.StatusMessage);

        // A double click covers one word
        await Pump(600);
        var zeile = At(window, view, 0, 20, 45);
        window.MouseDown(zeile, MouseButton.Left);
        window.MouseUp(zeile, MouseButton.Left);
        window.MouseDown(zeile, MouseButton.Left);
        window.MouseUp(zeile, MouseButton.Left);
        await Pump();
        Assert.Equal(2, vm.Document.Pages[0].Redactions.Count);
        AssertNear(new RectPt(9, 38.5, 27, 13), vm.Document.Pages[0].Redactions[1].Rect);

        // Covered words are gone for the select-text tool (and for copying)
        vm.ToggleSelectTextToolCommand.Execute(null);
        await Pump();
        Assert.Null(vm.ActiveRedaction);
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        await Pump();
        Assert.Equal("Welt", vm.SelectedText);
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task BlackOutWithSelectedTextCoversTheSelectionLineByLine()
    {
        var (window, view, dir) = await OpenAsync();
        using var _ = dir;
        var vm = window.ViewModel;
        vm.ToggleSelectTextToolCommand.Execute(null);
        await Pump();
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        await Pump();
        Assert.NotNull(vm.SelectedText);

        vm.RedactCommand.Execute(null);
        await Pump();
        var boxes = vm.Document!.Pages[0].Redactions;
        Assert.Equal(2, boxes.Count);
        AssertNear(new RectPt(9, 18.5, 87, 13), boxes[0].Rect);
        AssertNear(new RectPt(9, 38.5, 27, 13), boxes[1].Rect);
        Assert.All(boxes, b => Assert.Equal(RedactionFill.Black, b.Fill));
        // The selection is used up, the select-text tool stays on, the black-out tool did not switch on
        Assert.Null(vm.SelectedText);
        Assert.True(vm.IsSelectTextToolActive);
        Assert.Null(vm.ActiveRedaction);
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task OnARotatedPageTheBoxIsKeptInPageSpaceAndOtherToolsEndTheTool()
    {
        var (window, view, dir) = await OpenAsync();
        using var _ = dir;
        var vm = window.ViewModel;
        vm.RotateRightCommand.Execute(0);
        await Pump();
        vm.RedactCommand.Execute(null);
        await Pump();
        Drag(window, view, 0, (100, 20), (160, 50)); // shown 200 × 100 pt
        await Pump();
        var page = vm.Document!.Pages[0];
        var shown = new RectPt(100, 20, 60, 30);
        AssertNear(PageGeometry.Unrotate(shown, page.BaseSize, page.ExtraRotation), Assert.Single(page.Redactions).Rect);

        // Organize mode, the text tool and the button again each end the tool
        vm.ShowOrganizeCommand.Execute(null);
        Assert.Null(vm.ActiveRedaction);
        vm.ShowReadingCommand.Execute(null);
        vm.CoverCommand.Execute(null);
        vm.SelectTextToolCommand.Execute(null);
        Assert.Null(vm.ActiveRedaction);
        vm.CoverCommand.Execute(null);
        Assert.True(vm.IsCoverToolActive);
        Assert.False(vm.IsTextToolActive);
        vm.CoverCommand.Execute(null);
        Assert.Null(vm.ActiveRedaction);
        window.Close();
        await vm.ReleaseDocumentAsync();
    }
}
