using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Controls;
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Finding text (Ctrl+F, search bar) and the page sidebar whose width can be dragged.</summary>
public class SearchUiTests
{
    private static async Task Pump(int ms = 300)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    /// <summary>Three pages without a text layer; pages 1 and 3 get recognized words with "Welt" in them.</summary>
    private static async Task<(MainWindow Window, TempDir Dir)> OpenAsync()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        ToolTip.SetServiceEnabled(window, false);
        window.Show();
        var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        await Pump();
        var document = window.ViewModel.Document!;
        document.SetOcrWords(new Dictionary<int, IReadOnlyList<OcrWord>>
        {
            [document.Pages[0].Id] = [new OcrWord("Hallo", new RectPt(10, 20, 25, 10), 1), new OcrWord("Welt", new RectPt(40, 20, 20, 10), 1)],
            [document.Pages[2].Id] = [new OcrWord("Weltweit", new RectPt(10, 50, 40, 10), 1)],
        });
        await Pump();
        return (window, dir);
    }

    private static void Press(MainWindow window, Key key, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyPress(key, modifiers, PhysicalKey.None, null);

    [AvaloniaFact]
    public async Task CtrlFOpensTheSearchBarAndEnterAndF3WalkThroughTheHits()
    {
        var (window, dir) = await OpenAsync();
        using var _ = dir;
        var vm = window.ViewModel;
        var bar = window.FindControl<Border>("SearchBar")!;
        var box = window.FindControl<TextBox>("SearchBox")!;
        var view = window.FindControl<HorizontalPageView>("PageView")!;
        Assert.False(bar.IsVisible);

        Press(window, Key.F, RawInputModifiers.Control);
        await Pump();
        Assert.True(bar.IsVisible);
        Assert.True(box.IsFocused);

        window.KeyTextInput("welt");                         // upper and lower case are the same
        await Pump();
        Assert.Equal("welt", vm.SearchText);
        Assert.Equal(2, vm.SearchHits.Count);
        Assert.Equal(2, view.SearchHits.Count);              // highlighted in the reading view
        Assert.Equal("1 of 2", window.FindControl<TextBlock>("SearchStatus")!.Text);
        Assert.Same(vm.SearchHits[0], view.CurrentSearchHit);
        Assert.Equal(0, vm.CurrentPage);

        Press(window, Key.Enter);                            // next hit: on page 3 ("Weltweit")
        await Pump();
        Assert.Same(vm.SearchHits[1], vm.CurrentSearchHit);
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal("2 of 2", vm.SearchStatusText);

        Press(window, Key.F3);                               // F3 also in the box; after the last hit the first again
        await Pump();
        Assert.Same(vm.SearchHits[0], vm.CurrentSearchHit);
        Press(window, Key.Enter, RawInputModifiers.Shift);   // back: the last one
        await Pump();
        Assert.Same(vm.SearchHits[1], vm.CurrentSearchHit);

        Press(window, Key.Escape);                           // Esc closes the bar and removes the highlights
        await Pump();
        Assert.False(bar.IsVisible);
        Assert.Empty(view.SearchHits);
        Assert.Equal("welt", vm.SearchText);                 // kept for the next time
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task AHitInOrganizeModeIsShownInTheReadingViewAndEditsSearchAgain()
    {
        var (window, dir) = await OpenAsync();
        using var _ = dir;
        var vm = window.ViewModel;
        vm.ShowOrganizeCommand.Execute(null);
        vm.ShowSearchCommand.Execute(null);
        vm.SearchText = "weltweit";
        await Pump();
        Assert.False(vm.IsOrganizeMode);                     // hits are highlighted in the reading view
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(2, Assert.Single(vm.SearchHits).PageIndex);

        vm.DeletePagesCommand.Execute(0);                    // the page moves to index 1: searched again
        await Pump();
        Assert.Equal(1, Assert.Single(vm.SearchHits).PageIndex);

        vm.SearchText = "nirgends";
        await Pump();
        Assert.Empty(vm.SearchHits);
        Assert.Equal("No matches", vm.SearchStatusText);
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task ADocumentWithoutTextSaysSo()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        var vm = window.ViewModel;
        vm.ShowSearchCommand.Execute(null);
        vm.SearchText = "x";
        await Pump();
        Assert.Equal("No searchable text – recognize the text first", vm.SearchStatusText);
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task DraggingTheEdgeOfThePageSidebarMakesItAndItsThumbnailsWider()
    {
        var settings = new AppSettings();
        var window = new MainWindow(settings) { Width = 1100, Height = 700 };
        ToolTip.SetServiceEnabled(window, false);
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 12));
        await Pump();
        var panel = window.FindControl<Border>("PageSidebar")!;
        var sidebar = window.FindControl<ThumbnailGrid>("SidebarView")!;
        var splitter = window.FindControl<Thumb>("SidebarSplitter")!;
        var before = panel.Bounds.Width;
        int VisiblePages() => Enumerable.Range(0, (int)sidebar.Bounds.Height).Select(y => sidebar.PageIndexAt(new Point(sidebar.Bounds.Width / 2, y))).Where(i => i >= 0).Distinct().Count();
        var pagesBefore = VisiblePages();

        var start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(60, 0));
        await Pump(100);
        window.MouseMove(start + new Point(120, 0));
        await Pump(100);
        window.MouseUp(start + new Point(120, 0), MouseButton.Left);
        await Pump();

        Assert.InRange(panel.Bounds.Width, before + 110, before + 130);
        Assert.Equal(panel.Bounds.Width, settings.PageSidebarWidth, 1); // remembered
        Assert.True(VisiblePages() < pagesBefore);                      // larger thumbnails: fewer fit in

        // Never wider than half the window
        start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(800, 0));
        window.MouseUp(start + new Point(800, 0), MouseButton.Left);
        await Pump();
        Assert.True(panel.Bounds.Width <= window.Bounds.Width / 2 + 1);
        window.Close();
        await window.ViewModel.Document!.DisposeAsync();
    }
}
