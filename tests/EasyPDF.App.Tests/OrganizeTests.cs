using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using EasyPDF.App.Controls;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class OrganizeViewModelTests
{
    private static (MainViewModel Vm, FakeDialogs Dialogs) Make()
    {
        var dialogs = new FakeDialogs();
        return (new MainViewModel(new AppSettings(), dialogs), dialogs);
    }

    private static async Task<int> PageCountAsync(string path)
    {
        await using var s = await PdfSource.OpenAsync(path);
        return s.PageCount;
    }

    [Fact]
    public void PageSidebarIsRememberedInTheSettings()
    {
        var settings = new AppSettings { PageSidebarVisible = false };
        var vm = new MainViewModel(settings, new FakeDialogs());
        Assert.False(vm.IsPageSidebarVisible);
        vm.TogglePageSidebarCommand.Execute(null);
        Assert.True(settings.PageSidebarVisible);
        Assert.False(vm.PageSidebarShown);                    // no document: nothing to show
    }

    [Fact]
    public async Task ModeSwitchingAndSelectAll()
    {
        using var dir = new TempDir();
        var (vm, _) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 4));
        Assert.False(vm.IsOrganizeMode);
        Assert.True(vm.ReadingVisible);
        Assert.False(vm.SelectAllCommand.CanExecute(null)); // only in Organize mode

        var scrolled = -1;
        vm.ScrollToPageRequested += p => scrolled = p;
        vm.CurrentPage = 2;
        vm.ShowOrganizeCommand.Execute(null);
        Assert.True(vm.OrganizeVisible);
        Assert.Equal(2, scrolled); // the view that appears jumps to the current page

        vm.SelectAllCommand.Execute(null);
        Assert.Equal(4, vm.SelectedIds.Count);
        vm.SwitchModeCommand.Execute(3);
        Assert.False(vm.IsOrganizeMode);
        Assert.Equal(3, vm.CurrentPage);
        Assert.Equal(4, vm.SelectedIds.Count); // the selection survives the mode change
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task PageCommandsActOnTheSelectionInOrganizeMode()
    {
        using var dir = new TempDir();
        var (vm, _) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 6));
        vm.ShowOrganizeCommand.Execute(null);
        vm.SetSelection([vm.Document!.Pages[1].Id, vm.Document!.Pages[4].Id]);

        vm.RotateRightCommand.Execute(null);                          // no page given -> the selection
        Assert.Equal(new[] { 0, 90, 0, 0, 90, 0 }, vm.Document!.Pages.Select(p => p.ExtraRotation).ToArray());

        vm.RotateLeftCommand.Execute(4);                              // clicked page belongs to the selection -> all of it
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0 }, vm.Document!.Pages.Select(p => p.ExtraRotation).ToArray());

        vm.RotateRightCommand.Execute(0);                             // clicked page outside the selection -> only that page
        Assert.Equal(new[] { 90, 0, 0, 0, 0, 0 }, vm.Document!.Pages.Select(p => p.ExtraRotation).ToArray());

        vm.DeletePagesCommand.Execute(null);
        Assert.Equal(4, vm.Document!.PageCount);
        Assert.Empty(vm.SelectedIds);                                 // deleted pages leave the selection
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task DeletingEverythingIsRefusedWithAMessage()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 3));
        vm.ShowOrganizeCommand.Execute(null);
        vm.SelectAllCommand.Execute(null);
        await vm.DeletePagesCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Document!.PageCount);
        Assert.Single(dialogs.Errors);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task MovePagesIsOneUndoStep()
    {
        using var dir = new TempDir();
        var (vm, _) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 5));
        vm.MovePages([0, 1], 5);
        Assert.Equal(new[] { 2, 3, 4, 0, 1 }, vm.Document!.Pages.Select(p => p.SourceIndex).ToArray());
        vm.UndoCommand.Execute(null);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, vm.Document!.Pages.Select(p => p.SourceIndex).ToArray());
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task RotateDialogOffersTheSelection()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 5));
        vm.ShowOrganizeCommand.Execute(null);
        vm.SetSelection([vm.Document!.Pages[2].Id, vm.Document!.Pages[3].Id]);
        await vm.RotatePagesCommand.ExecuteAsync(null);
        Assert.Equal([2, 3], dialogs.LastRotateContext!.SelectedPages);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task InsertGoesBeforeTheFirstAndAfterTheLastSelectedPage()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 5));
        vm.ShowOrganizeCommand.Execute(null);
        vm.SetSelection([vm.Document!.Pages[1].Id, vm.Document!.Pages[3].Id]);

        dialogs.PickedFiles = [dir.Pdf("b.pdf", 1, 500)];
        await vm.InsertBeforeCommand.ExecuteAsync(null);
        Assert.Equal("b.pdf", vm.Document!.Pages[1].Source.FileName);

        dialogs.PickedFiles = [dir.Pdf("c.pdf", 1, 900)];
        await vm.InsertAfterCommand.ExecuteAsync(null);
        // selection was pages with ids of original 1 and 3 -> now at index 2 and 4; after the last one
        Assert.Equal("c.pdf", vm.Document!.Pages[5].Source.FileName);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task InsertOfASingleFileTakesOnlyTheChosenPages()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 3));

        dialogs.PickedFiles = [dir.Pdf("b.pdf", 5, 500)];
        dialogs.InsertRangeAnswer = [1, 3];
        await vm.InsertAfterCommand.ExecuteAsync(0);
        Assert.Equal(5, dialogs.LastInsertRangeContext!.PageCount);
        Assert.Equal("b.pdf", dialogs.LastInsertRangeContext.FileName);
        Assert.Equal(new[] { "a.pdf", "b.pdf", "b.pdf", "a.pdf", "a.pdf" }, vm.Document!.Pages.Select(p => p.Source.FileName).ToArray());
        Assert.Equal([1, 3], vm.Document!.Pages.Where(p => p.Source.FileName == "b.pdf").Select(p => p.SourceIndex).ToArray());

        // cancelling the range dialog inserts nothing
        dialogs.PickedFiles = [dir.Pdf("c.pdf", 2, 900)];
        dialogs.InsertRangeCancel = true;
        await vm.InsertAfterCommand.ExecuteAsync(0);
        Assert.Equal(5, vm.Document!.PageCount);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task InsertRangeDialogValidatesTheRange()
    {
        var vm = new InsertRangeDialogViewModel(new InsertRangeContext("x.pdf", 5));
        Assert.True(vm.IsValid);
        Assert.Equal([0, 1, 2, 3, 4], vm.BuildResult());

        vm.IsRange = true;
        Assert.False(vm.IsValid);               // empty range
        Assert.Null(vm.BuildResult());
        vm.RangeText = "2-3, 5";
        Assert.True(vm.IsValid);
        Assert.Equal([1, 2, 4], vm.BuildResult());
        vm.RangeText = "6";
        Assert.False(vm.IsValid);
        Assert.NotEmpty(vm.ErrorText);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ExtractWritesTheSelectedPagesAndLeavesTheDocumentAlone()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 6));
        vm.ShowOrganizeCommand.Execute(null);
        vm.SetSelection([vm.Document!.Pages[1].Id, vm.Document!.Pages[2].Id, vm.Document!.Pages[5].Id]);
        dialogs.SavePath = dir.File("extract.pdf");
        await vm.ExtractPagesCommand.ExecuteAsync(null);

        Assert.Equal(3, await PageCountAsync(dir.File("extract.pdf")));
        Assert.Equal(6, vm.Document!.PageCount);
        Assert.False(vm.IsDirty);
        Assert.NotEqual("", vm.StatusMessage);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task SplitWritesTheRequestedParts()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 7));
        dialogs.SplitAnswer = new SplitRequest(SplitPlan.EveryN(7, 3), dir.Path, "out");
        await vm.SplitDocumentCommand.ExecuteAsync(null);

        Assert.Equal(7, dialogs.LastSplitContext!.PageCount);
        Assert.Equal(3, await PageCountAsync(dir.File("out_part01.pdf")));
        Assert.Equal(1, await PageCountAsync(dir.File("out_part03.pdf")));
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task ConcatenateCreatesAnUnsavedDocumentIncludingTheEditedCurrentOne()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 3));
        vm.DeletePagesCommand.Execute(0);                              // unsaved edit: 2 pages left

        var b = await PdfSource.OpenAsync(dir.Pdf("b.pdf", 2, 500), null, TestContext.Current.CancellationToken);
        var c = await PdfSource.OpenAsync(dir.Pdf("c.pdf", 1, 900), null, TestContext.Current.CancellationToken);
        dialogs.ConcatAnswer = new ConcatenateResult([EditableDocument.FromSource(b), null, EditableDocument.FromSource(c)]);
        await vm.ConcatenateCommand.ExecuteAsync(null);

        Assert.True(dialogs.LastConcatContext!.HasCurrentDocument);
        Assert.Equal(0, dialogs.UnsavedAsked);                         // the edits go into the new document
        Assert.False(vm.Document!.HasFile);
        Assert.Equal(5, vm.Document!.PageCount);
        Assert.Equal(new[] { "b.pdf", "b.pdf", "a.pdf", "a.pdf", "c.pdf" }, vm.Document!.Pages.Select(p => p.Source.FileName).ToArray());
        Assert.True(vm.IsDirty);

        dialogs.SavePath = dir.File("all.pdf");
        await vm.SaveCommand.ExecuteAsync(null);                        // untitled -> asks for a path
        Assert.Equal(5, await PageCountAsync(dir.File("all.pdf")));
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task ConcatenateWithoutTheCurrentDocumentAsksAboutUnsavedChanges()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 3));
        vm.DeletePagesCommand.Execute(0);

        var b = await PdfSource.OpenAsync(dir.Pdf("b.pdf", 2, 500), null, TestContext.Current.CancellationToken);
        var c = await PdfSource.OpenAsync(dir.Pdf("c.pdf", 1, 900), null, TestContext.Current.CancellationToken);
        dialogs.ConcatAnswer = new ConcatenateResult([EditableDocument.FromSource(b), EditableDocument.FromSource(c)]);
        dialogs.UnsavedAnswer = UnsavedChoice.Cancel;
        await vm.ConcatenateCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.UnsavedAsked);
        Assert.Equal(2, vm.Document!.PageCount);                        // unchanged

        var b2 = await PdfSource.OpenAsync(dir.Pdf("b.pdf", 2, 500), null, TestContext.Current.CancellationToken);
        var c2 = await PdfSource.OpenAsync(dir.Pdf("c.pdf", 1, 900), null, TestContext.Current.CancellationToken);
        dialogs.ConcatAnswer = new ConcatenateResult([EditableDocument.FromSource(b2), EditableDocument.FromSource(c2)]);
        dialogs.UnsavedAnswer = UnsavedChoice.Discard;
        await vm.ConcatenateCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Document!.PageCount);
        Assert.Equal("b.pdf", vm.Document!.Pages[0].Source.FileName);
        await vm.Document!.DisposeAsync();
    }
}

public class SplitAndConcatenateDialogTests
{
    [Fact]
    public void SplitDialogValidatesInput()
    {
        using var dir = new TempDir();
        var vm = new SplitDialogViewModel(new SplitDialogContext(10, new int[0], dir.Path, "doc"));
        Assert.True(vm.EveryN);
        Assert.False(vm.CanSplitAtSelected);
        Assert.True(vm.IsValid);
        Assert.Equal(1, vm.PartCount);                     // default 10 pages per part

        vm.PagesPerPart = "3";
        Assert.Equal(4, vm.PartCount);
        vm.PagesPerPart = "0";
        Assert.False(vm.IsValid);
        vm.PagesPerPart = "x";
        Assert.False(vm.IsValid);
        vm.PagesPerPart = "3";
        vm.Folder = Path.Combine(dir.Path, "missing");
        Assert.False(vm.IsValid);
        vm.Folder = dir.Path;
        vm.BaseName = "a/b";
        Assert.False(vm.IsValid);
        vm.BaseName = "doc";
        Assert.True(vm.IsValid);

        File.WriteAllText(dir.File("doc_part02.pdf"), "x");  // would be overwritten
        Assert.False(vm.IsValid);
        Assert.Contains("doc_part02.pdf", vm.ErrorText);
    }

    [Fact]
    public void SplitDialogCanSplitAtSelectedPages()
    {
        using var dir = new TempDir();
        var vm = new SplitDialogViewModel(new SplitDialogContext(10, [3, 7], dir.Path, "doc"));
        Assert.True(vm.CanSplitAtSelected);
        Assert.False(vm.EveryN);                           // preselected when pages are selected
        var request = vm.BuildRequest();
        Assert.Equal([new SplitPart(0, 3), new SplitPart(3, 4), new SplitPart(7, 3)], request!.Parts);
        Assert.Equal("doc", request.BaseName);
    }

    [Fact]
    public async Task ConcatenateDialogCollectsOrdersAndValidates()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("bad.pdf"), "nope");
        var vm = new ConcatenateDialogViewModel(new ConcatenateContext(true), 4);
        Assert.True(vm.IncludeCurrent);
        Assert.Single(vm.Items);
        Assert.True(vm.Items[0].IsCurrent);
        Assert.False(vm.IsValid);                          // needs two entries

        await vm.AddFilesAsync([dir.Pdf("b.pdf", 2), dir.Pdf("a.pdf", 3)]);
        Assert.Equal(3, vm.Items.Count);
        Assert.Equal(9, vm.TotalPages);
        Assert.True(vm.IsValid);

        await vm.AddFilesAsync([dir.File("bad.pdf")]);
        Assert.False(vm.IsValid);                          // unreadable file blocks OK
        Assert.True(vm.Items.Last().HasError);
        vm.Remove(vm.Items.Last());
        Assert.True(vm.IsValid);

        vm.SortByName();
        Assert.Equal(new[] { true, false, false }, vm.Items.Select(i => i.IsCurrent).ToArray());
        Assert.Equal(new[] { "a.pdf", "b.pdf" }, vm.Items.Skip(1).Select(i => i.Name).ToArray());

        vm.MoveDown(vm.Items[0]);
        Assert.Equal("a.pdf", vm.Items[0].Name);
        vm.MoveUp(vm.Items[2]);
        Assert.Equal("b.pdf", vm.Items[1].Name);

        vm.IncludeCurrent = false;
        Assert.DoesNotContain(vm.Items, i => i.IsCurrent);
        Assert.True(vm.IsValid);
        var result = vm.BuildResult();
        Assert.Equal(2, result!.Parts.Count);
        Assert.All(result.Parts, Assert.NotNull);
        await vm.DisposeAllAsync();
    }

    [Fact]
    public async Task ConcatenateDialogAddsTheDocumentsOfOtherWindowsOnce()
    {
        using var dir = new TempDir();
        await using var b = EditableDocument.FromSource(await PdfSource.OpenAsync(dir.Pdf("b.pdf", 3), null, TestContext.Current.CancellationToken));
        await using var c = EditableDocument.FromSource(await PdfSource.OpenAsync(dir.Pdf("c.pdf", 2), null, TestContext.Current.CancellationToken));
        b.Rotate([0], 90);
        List<OpenDocumentInfo> others = [new("b.pdf", b), new("c.pdf", c)];
        var vm = new ConcatenateDialogViewModel(new ConcatenateContext(false, OtherDocuments: () => others), 0);
        Assert.True(vm.HasOtherDocuments);

        await vm.AddAllOpenDocumentsCommand.ExecuteAsync(null);
        await vm.AddAllOpenDocumentsCommand.ExecuteAsync(null);         // already listed: nothing added
        Assert.Equal(new[] { "b.pdf", "c.pdf" }, vm.Items.Select(i => i.Name).ToArray());
        Assert.Equal(new[] { Loc.T("ConcatFromWindowEdited"), Loc.T("ConcatFromWindow") }, vm.Items.Select(i => i.Folder).ToArray());
        Assert.Equal(5, vm.TotalPages);
        Assert.True(vm.IsListed(others[0]));

        var result = vm.BuildResult()!;
        Assert.Equal(90, result.Parts[0]!.Pages[0].ExtraRotation);       // the edits of the window are taken along
        Assert.DoesNotContain(result.Parts[0]!.Sources, s => b.Sources.Contains(s));
        foreach (var part in result.Parts)
            await part!.DisposeAsync();
    }

    [Fact]
    public void ConcatenateDialogWithoutOtherWindowsHidesTheirButton()
    {
        Assert.False(new ConcatenateDialogViewModel(new ConcatenateContext(true), 1).HasOtherDocuments);
        Assert.False(new ConcatenateDialogViewModel(new ConcatenateContext(true, OtherDocuments: () => []), 1).HasOtherDocuments);
    }

    [Fact]
    public void ConcatenateDialogWithoutCurrentDocument()
    {
        var vm = new ConcatenateDialogViewModel(new ConcatenateContext(false), 0);
        Assert.False(vm.IncludeCurrent);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void ConcatenateRowCountsOnePageInTheSingular()
    {
        Assert.Equal("1 page", ConcatItem.Current(1).PagesText);
        Assert.Equal("2 pages", ConcatItem.Current(2).PagesText);
    }
}

public class OrganizeUiTests
{
    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static async Task<(MainWindow Window, ThumbnailGrid Grid)> OpenOrganizeAsync(TempDir dir, int pages = 12)
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", pages));
        window.ViewModel.ShowOrganizeCommand.Execute(null);
        await Pump(400);
        return (window, window.FindControl<ThumbnailGrid>("OrganizeView")!);
    }

    /// <summary>
    /// Number of clearly red pixels in a rendered frame below <paramref name="fromY"/> (the red page frame is antialiased, so no exact
    /// colour match). Pass the top of the content area to leave out the ribbon, whose icons use red too (Delete).
    /// </summary>
    private static unsafe int CountRedPixels(Avalonia.Media.Imaging.WriteableBitmap frame, int fromY = 0)
    {
        using var fb = frame.Lock();
        var bgra = fb.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        var count = 0;
        for (var y = fromY; y < fb.Size.Height; y++)
        {
            var row = (byte*)fb.Address + y * fb.RowBytes;
            for (var x = 0; x < fb.Size.Width; x++)
            {
                var p = row + x * 4;
                var (r, b) = bgra ? (p[2], p[0]) : (p[0], p[2]);
                if (r >= 200 && p[1] <= 100 && b <= 100)
                    count++;
            }
        }
        return count;
    }

    /// <summary>Window coordinates of a point inside the cell of a page (the grid uses 174 x 236 cells).</summary>
    private static Point InCell(MainWindow window, ThumbnailGrid grid, int index, double fractionX = 0.5)
    {
        var origin = grid.TranslatePoint(new Point(0, 0), window)!.Value;
        var surfaceWidth = grid.Bounds.Width - 17; // vertical scroll bar
        var columns = (int)(surfaceWidth / 174);
        var offsetX = (surfaceWidth - columns * 174) / 2;
        return new Point(origin.X + offsetX + index % columns * 174 + 174 * fractionX, origin.Y + 6 + index / columns * 236 + 100);
    }

    [AvaloniaFact]
    public async Task ClickCtrlClickAndShiftClickSelectPages()
    {
        using var dir = new TempDir();
        var (window, grid) = await OpenOrganizeAsync(dir);
        var vm = window.ViewModel;
        var pages = vm.Document!.Pages;

        window.MouseDown(InCell(window, grid, 1), MouseButton.Left);
        window.MouseUp(InCell(window, grid, 1), MouseButton.Left);
        Assert.Equal(new[] { pages[1].Id }, vm.SelectedIds.ToArray());

        window.MouseDown(InCell(window, grid, 3), MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(InCell(window, grid, 3), MouseButton.Left, RawInputModifiers.Control);
        Assert.Equal(2, vm.SelectedIds.Count);

        await Task.Delay(700); // a second click on the same page within the double-click time would open it in Reading mode
        window.MouseDown(InCell(window, grid, 3), MouseButton.Left, RawInputModifiers.Control); // toggle off
        window.MouseUp(InCell(window, grid, 3), MouseButton.Left, RawInputModifiers.Control);
        Assert.Equal(new[] { pages[1].Id }, vm.SelectedIds.ToArray());

        window.MouseDown(InCell(window, grid, 5), MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(InCell(window, grid, 5), MouseButton.Left, RawInputModifiers.Shift);
        Assert.Equal(3, vm.SelectedIds.Count);                                                    // range from the last clicked page (3) to 5

        window.MouseDown(new Point(grid.TranslatePoint(new Point(0, 0), window)!.Value.X + 3, grid.TranslatePoint(new Point(0, 0), window)!.Value.Y + grid.Bounds.Height - 4), MouseButton.Left);
        window.MouseUp(new Point(grid.TranslatePoint(new Point(0, 0), window)!.Value.X + 3, grid.TranslatePoint(new Point(0, 0), window)!.Value.Y + grid.Bounds.Height - 4), MouseButton.Left);
        Assert.Empty(vm.SelectedIds);                                                             // click on empty space

        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task DraggingAPageMovesItAndUndoRestoresIt()
    {
        using var dir = new TempDir();
        var (window, grid) = await OpenOrganizeAsync(dir);
        var vm = window.ViewModel;

        var from = InCell(window, grid, 0);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 20, from.Y + 5));
        window.MouseMove(InCell(window, grid, 4, 0.2));
        window.MouseUp(InCell(window, grid, 4, 0.2), MouseButton.Left);
        await Pump();

        Assert.Equal(new[] { 1, 2, 3, 0, 4 }, vm.Document!.Pages.Take(5).Select(p => p.SourceIndex).ToArray());
        Assert.True(vm.IsDirty);
        vm.UndoCommand.Execute(null);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, vm.Document!.Pages.Take(5).Select(p => p.SourceIndex).ToArray());
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task DraggingASelectionMovesAllSelectedPagesTogether()
    {
        using var dir = new TempDir();
        var (window, grid) = await OpenOrganizeAsync(dir);
        var vm = window.ViewModel;

        foreach (var index in new[] { 0, 2 })
        {
            window.MouseDown(InCell(window, grid, index), MouseButton.Left, RawInputModifiers.Control);
            window.MouseUp(InCell(window, grid, index), MouseButton.Left, RawInputModifiers.Control);
        }
        var from = InCell(window, grid, 2);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 25, from.Y));
        window.MouseMove(InCell(window, grid, 5, 0.8));          // after page 5
        window.MouseUp(InCell(window, grid, 5, 0.8), MouseButton.Left);
        await Pump();

        Assert.Equal(new[] { 1, 3, 4, 5, 0, 2 }, vm.Document!.Pages.Take(6).Select(p => p.SourceIndex).ToArray());
        Assert.Equal(2, vm.SelectedIds.Count);                    // still selected
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task ClickingWithoutMovingKeepsOrderAndPlainClickInSelectionSelectsOnlyThatPage()
    {
        using var dir = new TempDir();
        var (window, grid) = await OpenOrganizeAsync(dir);
        var vm = window.ViewModel;
        vm.SetSelection([vm.Document!.Pages[0].Id, vm.Document!.Pages[1].Id]);

        window.MouseDown(InCell(window, grid, 1), MouseButton.Left);
        window.MouseUp(InCell(window, grid, 1), MouseButton.Left);
        Assert.Equal(new[] { vm.Document!.Pages[1].Id }, vm.SelectedIds.ToArray());
        Assert.False(vm.IsDirty);
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task DoubleClickOpensThePageInReadingMode()
    {
        using var dir = new TempDir();
        var (window, grid) = await OpenOrganizeAsync(dir);
        var vm = window.ViewModel;
        var p = InCell(window, grid, 7);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();
        Assert.False(vm.IsOrganizeMode);
        Assert.Equal(7, vm.CurrentPage);
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task RightClickSelectsTheUnselectedPageAndSetsTheContextPage()
    {
        using var dir = new TempDir();
        var (window, grid) = await OpenOrganizeAsync(dir);
        var vm = window.ViewModel;
        var p = InCell(window, grid, 5);
        window.MouseDown(p, MouseButton.Right);
        window.MouseUp(p, MouseButton.Right);
        Assert.Equal(5, grid.ContextPage);
        Assert.Equal(new[] { vm.Document!.Pages[5].Id }, vm.SelectedIds.ToArray());
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task OrganizeModeDrawsNoRedFrameOnlyTheSelection()
    {
        using var dir = new TempDir();
        var (window, grid) = await OpenOrganizeAsync(dir);
        var p = InCell(window, grid, 3);
        window.MouseDown(p, MouseButton.Right);
        window.MouseUp(p, MouseButton.Right);
        await Pump();
        // The right-clicked page is shown as selected; the red frame of the reading view does not appear here
        Assert.Equal(new[] { window.ViewModel.Document!.Pages[3].Id }, window.ViewModel.SelectedIds.ToArray());
        var contentTop = (int)grid.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.Equal(0, CountRedPixels(window.CaptureRenderedFrame()!, contentTop));

        // Control: the reading view does draw the red frame for the marked page
        window.ViewModel.MarkedPageId = window.ViewModel.Document!.Pages[3].Id; // page 4 is current, so it is visible
        window.ViewModel.ShowReadingCommand.Execute(null);
        await Pump(400);
        Assert.True(CountRedPixels(window.CaptureRenderedFrame()!, contentTop) > 0);
        window.Close();
        await window.ViewModel.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task OrganizeRibbonTabHasModeAndOutputCommands()
    {
        using var dir = new TempDir();
        var (window, _) = await OpenOrganizeAsync(dir, 3);
        var ribbon = window.GetVisualDescendants().OfType<EasyPDF.App.Controls.Ribbon.Ribbon>().Single();
        ribbon.SelectedIndex = 1;
        await Pump();
        var commands = window.GetVisualDescendants().OfType<EasyPDF.App.Controls.Ribbon.RibbonButton>().Select(b => b.Command).ToList();
        Assert.Contains(window.ViewModel.ConcatenateCommand, commands);
        Assert.Contains(window.ViewModel.ExtractPagesCommand, commands);
        Assert.Contains(window.ViewModel.SplitDocumentCommand, commands);
        Assert.Contains(window.ViewModel.SelectAllCommand, commands);
        Assert.Contains(window.ViewModel.ShowOrganizeCommand, commands);
        window.Close();
        await window.ViewModel.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task ThumbnailsRenderLazilyForTheWholeDocument()
    {
        using var dir = new TempDir();
        var (window, grid) = await OpenOrganizeAsync(dir, 40);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        // scroll down by wheel to the end and back without errors
        var p = InCell(window, grid, 2);
        for (var i = 0; i < 10; i++)
        {
            window.MouseWheel(p, new Vector(0, -1));
            await Pump(40);
        }
        window.Close();
        await window.ViewModel.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task ZoomCommandsChangeTheThumbnailSizeInOrganizeMode()
    {
        using var dir = new TempDir();
        var (window, grid) = await OpenOrganizeAsync(dir);
        var vm = window.ViewModel;
        var readingZoom = vm.EffectiveZoomPercent;
        int Columns() => Enumerable.Range(0, (int)grid.Bounds.Width).Select(x => grid.PageIndexAt(new Point(x, 100))).Where(i => i >= 0).Distinct().Count();
        var columns = Columns();
        Assert.Equal(100, grid.ZoomPercent);
        Assert.Equal("100", vm.ZoomInput);
        Assert.False(vm.FitWidthCommand.CanExecute(null));      // fit modes only exist for the reading view

        vm.ZoomInCommand.Execute(null);
        await Pump();
        Assert.Equal(125, grid.ZoomPercent);
        Assert.Equal("125", vm.ZoomInput);
        Assert.Contains("125", vm.StatusZoomText);
        Assert.True(Columns() < columns);                      // bigger thumbnails, fewer per row
        Assert.Equal(readingZoom, vm.EffectiveZoomPercent);    // the reading view keeps its zoom

        vm.ZoomOutCommand.Execute(null);
        vm.ZoomOutCommand.Execute(null);
        await Pump();
        Assert.Equal(75, grid.ZoomPercent);
        Assert.True(Columns() > columns);

        vm.ActualSizeCommand.Execute(null);
        Assert.Equal(100, grid.ZoomPercent);
        vm.ZoomInput = "1000";
        vm.ApplyZoomInputCommand.Execute(null);
        Assert.Equal(ThumbnailGrid.MaxZoomPercent, grid.ZoomPercent);
        Assert.Equal("300", vm.ZoomInput);

        var center = grid.TranslatePoint(new Point(grid.Bounds.Width / 2, grid.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(center, new Vector(0, -3), RawInputModifiers.Control);   // Ctrl+wheel zooms the thumbnails
        await Pump();
        Assert.True(grid.ZoomPercent < 300);
        Assert.Equal(grid.ZoomPercent, vm.ThumbnailZoomPercent);
        Assert.Equal(grid.ZoomPercent.ToString("0"), vm.ZoomInput);
        Assert.NotEqual(Math.Round(grid.ZoomPercent), grid.ZoomPercent);              // wheel zoom gives a fraction ...
        Assert.Equal(Loc.F("StatusZoom", vm.ZoomInput), vm.StatusZoomText);          // ... the status bar shows the same whole number as the ribbon

        vm.ShowReadingCommand.Execute(null);
        Assert.Equal(vm.EffectiveZoomPercent.ToString("0"), vm.ZoomInput);
        Assert.True(vm.FitWidthCommand.CanExecute(null));
        window.Close();
        await vm.ReleaseDocumentAsync();
    }
}
