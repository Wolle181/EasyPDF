using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>"Replace pages": consecutive pages give way to pages of another open document or a file.</summary>
public class ReplacePagesTests
{
    private static async Task<EditableDocument> OpenAsync(string path)
    {
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    private static int[] Widths(EditableDocument d) => [.. d.Pages.Select(p => (int)p.DisplaySize.Width)];

    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static Point CenterOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    [Fact]
    public async Task OnlyConsecutivePagesCanBeReplaced()
    {
        using var dir = new TempDir();
        await using var vm = new ReplacePagesDialogViewModel(new ReplacePagesContext(6, 2, [1, 2, 4]));
        Assert.True(vm.IsSelected);                              // the selection is preselected …
        Assert.Equal(Loc.T("ErrReplaceNotConsecutive"), vm.TargetErrorText);   // … but has a gap
        Assert.False(vm.IsValid);

        vm.IsCurrent = true;
        Assert.Empty(vm.TargetErrorText);
        Assert.False(vm.IsValid);                                // nothing to insert chosen yet
        Assert.Equal(Loc.T("ReplaceChooseSource"), vm.SummaryText);
        Assert.False(vm.HasOpenDocuments);
        Assert.True(vm.UseFile);

        vm.IsTargetRange = true;
        vm.TargetRangeText = "5, 3-4";
        Assert.Empty(vm.TargetErrorText);
        vm.TargetRangeText = "2, 4";
        Assert.Equal(Loc.T("ErrReplaceNotConsecutive"), vm.TargetErrorText);
        vm.TargetRangeText = "9";
        Assert.NotEmpty(vm.TargetErrorText);
        Assert.NotEqual(Loc.T("ErrReplaceNotConsecutive"), vm.TargetErrorText);
    }

    [Fact]
    public async Task AChosenFileIsHandedOverOnOkAndClosedOtherwise()
    {
        using var dir = new TempDir();
        var vm = new ReplacePagesDialogViewModel(new ReplacePagesContext(6, 2, []));
        vm.IsTargetRange = true;
        vm.TargetRangeText = "3-5";

        var text = dir.File("note.txt");
        File.WriteAllText(text, "no pdf");
        await vm.SetFileAsync(text);
        Assert.Equal(Loc.T("ErrNotAPdf"), vm.SourceErrorText);
        Assert.False(vm.IsValid);

        await vm.SetFileAsync(dir.Pdf("b.pdf", 4, 500));
        Assert.Empty(vm.SourceErrorText);
        Assert.Equal(Loc.F("ReplaceDocumentEntry", "b.pdf", Loc.Pages(4)), vm.FileText);
        vm.IsInsertRange = true;
        vm.InsertRangeText = "4, 1";
        Assert.Equal(Loc.F("ReplaceSummary", 3, 2, "b.pdf"), vm.SummaryText);

        var result = await vm.BuildResultAsync();
        Assert.NotNull(result);
        Assert.Equal((2, 3), (result!.First, result.Count));
        Assert.Equal([0, 3], result.Pages);                      // page order as in the file, like Insert
        await vm.DisposeAsync();                                 // must not close the handed-over file
        Assert.Equal(4, result.Replacement.PageCount);
        await result.Replacement.DisposeAsync();
    }

    [Fact]
    public async Task AnotherWindowsDocumentIsPreselectedAndCopiedWithItsChanges()
    {
        using var dir = new TempDir();
        await using var other = await OpenAsync(dir.Pdf("b.pdf", 3, 500));
        other.Delete([0]);                                       // unsaved change: 610 620
        var context = new ReplacePagesContext(4, 0, [], () => [new OpenDocumentInfo("b.pdf", other)]);
        await using var vm = new ReplacePagesDialogViewModel(context);
        Assert.True(vm.HasOpenDocuments);
        Assert.True(vm.UseOpenDocument);
        Assert.Equal(Loc.F("ReplaceDocumentEntry", "b.pdf", Loc.Pages(2)), vm.SelectedOpenDocument!.Label);
        Assert.True(vm.IsValid);

        var result = await vm.BuildResultAsync();
        Assert.NotSame(other, result!.Replacement);              // a copy: independent of the other window
        foreach (var source in result.Replacement.Sources)
            await source.LoadPageSizesAsync(token: TestContext.Current.CancellationToken);
        Assert.Equal([610, 620], Widths(result.Replacement));
        Assert.Equal([0, 1], result.Pages);
        await result.Replacement.DisposeAsync();
    }

    [Fact]
    public async Task TheCommandReplacesThePagesInOneUndoStep()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new AppSettings(), dialogs);
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 5));             // 100 110 120 130 140
        var b = dir.Pdf("b.pdf", 2, 500);                        // 600 610
        dialogs.ReplaceAnswer = async context => new ReplacePagesResult(1, 3, await OpenAsync(b), [1, 0]);

        await vm.ReplacePagesCommand.ExecuteAsync(3);
        Assert.Equal(3, dialogs.LastReplaceContext!.CurrentPage);   // the right-clicked page
        Assert.Equal([100, 610, 600, 140], Widths(vm.Document!));
        Assert.True(vm.Document!.IsDirty);
        vm.UndoCommand.Execute(null);
        Assert.Equal([100, 110, 120, 130, 140], Widths(vm.Document!));

        dialogs.ReplaceAnswer = null;                            // cancelled: nothing changes
        await vm.ReplacePagesCommand.ExecuteAsync(null);
        Assert.Equal(5, vm.Document!.PageCount);
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TheDialogReturnsTheChoiceOnOkAndNothingOnClose()
    {
        using var dir = new TempDir();
        var owner = new Window { Width = 700, Height = 700 };
        owner.Show();

        var dialog = new ReplacePagesDialog(new ReplacePagesContext(5, 1, []));
        var result = dialog.ShowDialog<ReplacePagesResult?>(owner);
        await Pump();
        var ok = dialog.FindControl<Button>("OkButton")!;
        Assert.False(ok.IsEnabled);                              // no file yet
        await dialog.Model.SetFileAsync(dir.Pdf("b.pdf", 3, 500));
        await Pump();
        Assert.True(ok.IsEnabled);
        Assert.Equal(Loc.F("ReplaceSummary", 1, 3, "b.pdf"), dialog.FindControl<TextBlock>("SummaryText")!.Text);
        dialog.MouseDown(CenterOf(ok, dialog), MouseButton.Left);
        dialog.MouseUp(CenterOf(ok, dialog), MouseButton.Left);
        var answer = await result;
        Assert.Equal((1, 1), (answer!.First, answer.Count));
        Assert.Equal([0, 1, 2], answer.Pages);
        await answer.Replacement.DisposeAsync();

        var closed = new ReplacePagesDialog(new ReplacePagesContext(5, 1, []));
        var none = closed.ShowDialog<ReplacePagesResult?>(owner);
        await Pump();
        await closed.Model.SetFileAsync(dir.Pdf("c.pdf", 1));
        closed.Close();                                          // [X] = cancel, the chosen file is closed again
        Assert.Null(await none);
        await Pump();
        File.Delete(dir.File("c.pdf"));                          // not held open any more
        owner.Close();
    }
}
