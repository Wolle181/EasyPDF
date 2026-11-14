using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using EasyPDF.App.Controls;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Localization;
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>The select-text tool: dragging over a page highlights its text, so one sees what was recognized.</summary>
public class SelectTextTests
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

    private static async Task<(MainWindow Window, HorizontalPageView View, TempDir Dir)> OpenWithWordsAsync(bool secondPageText = false)
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2)); // pages of 100 × 200 pt without a text layer
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
        if (secondPageText)
            document.SetOcrWords(new Dictionary<int, IReadOnlyList<OcrWord>>
            {
                [document.Pages[1].Id] = [new OcrWord("Seite", new RectPt(10, 20, 25, 10), 1), new OcrWord("zwei", new RectPt(40, 20, 20, 10), 1)],
            });
        await Pump();
        return (window, window.FindControl<HorizontalPageView>("PageView")!, dir);
    }

    [AvaloniaFact]
    public async Task DraggingOverRecognizedWordsSelectsThemAndCtrlCCopiesThem()
    {
        var (window, view, dir) = await OpenWithWordsAsync();
        using var _ = dir;
        var vm = window.ViewModel;
        var button = window.FindControl<RibbonButton>("SelectTextButton")!;
        Assert.Same(vm.ToggleSelectTextToolCommand, button.Command);
        button.Command!.Execute(null);
        await Pump();
        Assert.True(vm.IsSelectTextToolActive);
        Assert.True(view.IsSelectTextToolActive);
        Assert.True(button.IsActive);
        Assert.Equal(Loc.T("SelectTextToolSelected"), vm.StatusMessage);

        // From the start of "Hallo" into "schöne": both are selected, the content does not move
        var scroll = view.ScrollOffset;
        window.MouseDown(At(window, view, 0, 11, 25), MouseButton.Left);
        window.MouseMove(At(window, view, 0, 30, 26));
        window.MouseMove(At(window, view, 0, 55, 25));
        window.MouseUp(At(window, view, 0, 55, 25), MouseButton.Left);
        await Pump();
        Assert.Equal("Hallo schöne", vm.SelectedText);
        Assert.Equal(scroll, view.ScrollOffset);
        Assert.True(vm.CopyTextCommand.CanExecute(null));

        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await Pump();
        Assert.Equal("Hallo schöne", await window.Clipboard!.TryGetTextAsync());
        Assert.Equal(Loc.F("MsgSelectionCopied", Loc.Characters(12)), vm.StatusMessage);

        // Ctrl+A: the whole text of the page, line by line
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        await Pump();
        Assert.Equal("Hallo schöne Welt\nZeile", vm.SelectedText);

        // Double click selects one word; a click elsewhere removes the selection
        var welt = At(window, view, 0, 85, 25);
        window.MouseDown(welt, MouseButton.Left);
        window.MouseUp(welt, MouseButton.Left);
        window.MouseDown(welt, MouseButton.Left);
        window.MouseUp(welt, MouseButton.Left);
        await Pump();
        Assert.Equal("Welt", vm.SelectedText);
        await Pump(600); // not a third click of the same double click
        window.MouseDown(At(window, view, 0, 50, 150), MouseButton.Left);
        window.MouseUp(At(window, view, 0, 50, 150), MouseButton.Left);
        await Pump();
        Assert.Null(vm.SelectedText);
        Assert.False(vm.CopyTextCommand.CanExecute(null));

        // Esc removes the selection first, then ends the tool
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        await Pump();
        Assert.NotNull(vm.SelectedText);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump();
        Assert.Null(vm.SelectedText);
        Assert.True(vm.IsSelectTextToolActive);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump();
        Assert.False(vm.IsSelectTextToolActive);
        Assert.False(view.IsSelectTextToolActive);

        // Without the tool a drag moves the content again and selects nothing
        window.MouseDown(At(window, view, 0, 11, 25), MouseButton.Left);
        window.MouseMove(At(window, view, 0, 60, 25));
        window.MouseUp(At(window, view, 0, 60, 25), MouseButton.Left);
        await Pump();
        Assert.Null(vm.SelectedText);
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TheSelectionGoesOnAcrossPages()
    {
        var (window, view, dir) = await OpenWithWordsAsync(secondPageText: true);
        using var _ = dir;
        var vm = window.ViewModel;
        vm.ToggleSelectTextToolCommand.Execute(null);
        await Pump();

        // From "Welt" on the first page to "Seite" on the second: the rest of the first page, then the start of the second
        window.MouseDown(At(window, view, 0, 80, 25), MouseButton.Left);
        window.MouseMove(At(window, view, 0, 95, 60));
        window.MouseMove(At(window, view, 1, 20, 25));
        await Pump();
        Assert.Equal("Welt\nZeile\n\nSeite", vm.SelectedText);
        // ... and back again to the first page, now only there
        window.MouseMove(At(window, view, 0, 50, 25));
        window.MouseUp(At(window, view, 0, 50, 25), MouseButton.Left);
        await Pump();
        Assert.Equal("schöne Welt", vm.SelectedText);

        // Backwards from the second page into the first one
        window.MouseDown(At(window, view, 1, 50, 25), MouseButton.Left);
        window.MouseMove(At(window, view, 1, 20, 25));
        window.MouseMove(At(window, view, 0, 20, 45));
        window.MouseUp(At(window, view, 0, 20, 45), MouseButton.Left);
        await Pump();
        Assert.Equal("Zeile\n\nSeite zwei", vm.SelectedText);

        // A page change drops the selection
        vm.Document!.Rotate([1], 90);
        await Pump();
        Assert.Null(vm.SelectedText);
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task NearTheEdgeTheContentScrollsOnWhileSelecting()
    {
        var (window, view, dir) = await OpenWithWordsAsync(secondPageText: true);
        using var _ = dir;
        var vm = window.ViewModel;
        vm.FitWidthCommand.Execute(null); // the first page fills the width of the view
        await Pump();
        vm.GoToPage(0);
        var middle = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(middle, new Vector(0, 50), RawInputModifiers.Shift); // up to the top of the page
        await Pump();
        vm.ToggleSelectTextToolCommand.Execute(null);
        await Pump();
        Assert.True(view.PageRectInViewport(1).X > view.Bounds.Width - 20); // the second page is (almost) out of view

        var scroll = view.ScrollOffset;
        window.MouseDown(At(window, view, 0, 11, 25), MouseButton.Left);
        var edge = view.TranslatePoint(new Point(view.Bounds.Width - 30, view.Bounds.Height / 3), window)!.Value;
        window.MouseMove(edge);
        for (var i = 0; i < 100 && view.PageRectInViewport(1).Right > view.Bounds.Width - 20; i++)
            await Pump(100);
        Assert.True(view.ScrollOffset.X > scroll.X);
        Assert.StartsWith("Hallo schöne Welt\nZeile\n\nSeite", vm.SelectedText);
        window.MouseUp(edge, MouseButton.Left);
        await Pump();
        var stopped = view.ScrollOffset;
        await Pump(300);
        Assert.Equal(stopped, view.ScrollOffset); // released: no more scrolling
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task APageWithoutTextSaysSo()
    {
        var (window, view, dir) = await OpenWithWordsAsync();
        using var _ = dir;
        var vm = window.ViewModel;
        vm.ToggleSelectTextToolCommand.Execute(null);
        await Pump();
        Assert.True(view.PageRectInViewport(1).Right < view.Bounds.Width); // the second page is visible

        // The second page has neither recognized words nor a text layer
        window.MouseDown(At(window, view, 1, 50, 100), MouseButton.Left);
        window.MouseMove(At(window, view, 1, 80, 120));
        window.MouseUp(At(window, view, 1, 80, 120), MouseButton.Left);
        await Pump();
        Assert.Equal(Loc.F("MsgPageHasNoText", 2), vm.StatusMessage);
        Assert.Null(vm.SelectedText);
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task OtherToolsAndOrganizeModeEndTheTool()
    {
        var (window, view, dir) = await OpenWithWordsAsync();
        using var _ = dir;
        var vm = window.ViewModel;
        vm.ToggleSelectTextToolCommand.Execute(null);
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        await Pump();
        Assert.NotNull(vm.SelectedText);

        vm.SelectTextToolCommand.Execute(null); // Insert text
        Assert.False(vm.IsSelectTextToolActive);
        Assert.Null(vm.SelectedText);
        Assert.True(vm.IsTextToolActive);

        vm.ToggleSelectTextToolCommand.Execute(null);
        Assert.True(vm.IsSelectTextToolActive);
        Assert.False(vm.IsTextToolActive);
        vm.IsOrganizeMode = true;
        Assert.False(vm.IsSelectTextToolActive);

        // The tool switches back to the reading view
        vm.ToggleSelectTextToolCommand.Execute(null);
        Assert.False(vm.IsOrganizeMode);
        Assert.True(vm.IsSelectTextToolActive);

        var edit = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Edit").Menu!;
        Assert.Contains(edit.Items.OfType<NativeMenuItem>(), i => i.Header == "Select Text" && i.Command == vm.ToggleSelectTextToolCommand);
        window.Close();
        await vm.Document!.DisposeAsync();
    }
}
