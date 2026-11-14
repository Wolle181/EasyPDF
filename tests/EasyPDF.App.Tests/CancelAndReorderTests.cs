using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls;
using EasyPDF.App.Dialogs;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class CancelTests
{
    [Fact]
    public async Task CancelStopsALongExtractWithoutAnErrorAndLeavesNoFile()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new AppSettings(), dialogs);
        await vm.OpenPathAsync(dir.Pdf("big.pdf", 20000));
        dialogs.SavePath = dir.File("out.pdf");
        vm.ShowOrganizeCommand.Execute(null);
        vm.SelectAllCommand.Execute(null);
        Assert.False(vm.CancelOperationCommand.CanExecute(null)); // nothing running

        var running = vm.ExtractPagesCommand.ExecuteAsync(null);
        for (var i = 0; i < 200 && !vm.IsBusy; i++)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(vm.IsBusy);
        Assert.Equal("Extracting 20000 pages…", vm.BusyText);    // the status bar says what is running
        Assert.EndsWith("%", vm.ProgressText);
        Assert.True(vm.CancelOperationCommand.CanExecute(null));
        vm.CancelOperationCommand.Execute(null);
        await running;

        Assert.False(vm.IsBusy);
        Assert.Empty(dialogs.Errors);
        Assert.False(System.IO.File.Exists(dir.File("out.pdf")));
        Assert.Empty(System.IO.Directory.GetFiles(dir.Path, ".*.tmp"));
        Assert.NotEqual("", vm.StatusMessage);
        Assert.False(vm.CancelOperationCommand.CanExecute(null));
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task ACancelledSaveKeepsTheDocumentDirtyAndTheFileUntouched()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new AppSettings(), dialogs);
        var path = dir.Pdf("big.pdf", 20000);
        var length = new System.IO.FileInfo(path).Length;
        await vm.OpenPathAsync(path);
        vm.MovePages([19999], 0);

        var running = vm.SaveCommand.ExecuteAsync(null);
        for (var i = 0; i < 200 && !vm.IsBusy; i++)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        vm.CancelOperationCommand.Execute(null);
        await running;

        Assert.False(vm.IsBusy);
        Assert.True(vm.IsDirty);
        Assert.Empty(dialogs.Errors);
        Assert.Equal(length, new System.IO.FileInfo(path).Length);
        await vm.Document!.DisposeAsync();
    }
}

public class ConcatenateReorderTests
{
    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task MoveToUsesInsertPositionsOfTheListBeforeTheMove()
    {
        using var dir = new TempDir();
        var vm = new ConcatenateDialogViewModel(new(false), 0);
        await vm.AddFilesAsync([dir.Pdf("a.pdf", 1), dir.Pdf("b.pdf", 1), dir.Pdf("c.pdf", 1), dir.Pdf("d.pdf", 1)]);
        string Order() => string.Join(",", vm.Items.Select(i => i.Name[0]));

        vm.MoveTo(vm.Items[0], 3);        // before c (own position 0 -> ends up at 2)
        Assert.Equal("b,c,a,d", Order());
        vm.MoveTo(vm.Items[2], 0);        // a to the front
        Assert.Equal("a,b,c,d", Order());
        vm.MoveTo(vm.Items[1], 4);        // b to the end
        Assert.Equal("a,c,d,b", Order());
        vm.MoveTo(vm.Items[1], 1);        // c onto its own position: no change
        vm.MoveTo(vm.Items[1], 2);
        Assert.Equal("a,c,d,b", Order());
        await vm.DisposeAllAsync();
    }

    [AvaloniaFact]
    public async Task DraggingARowReordersTheList()
    {
        using var dir = new TempDir();
        var dialog = new ConcatenateDialog(new(false), 0);
        dialog.Show();
        await dialog.Model.AddFilesAsync([dir.Pdf("a.pdf", 1), dir.Pdf("b.pdf", 1), dir.Pdf("c.pdf", 1)]);
        await Pump();
        string Order() => string.Join(",", dialog.Model.Items.Select(i => i.Name[0]));
        Assert.Equal("a,b,c", Order());

        var list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
        var rows = dialog.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Point Center(ListBoxItem row, double fraction = 0.5) =>
            row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height * fraction), dialog)!.Value;

        var from = Center(rows[0]);
        dialog.MouseDown(from, MouseButton.Left);
        dialog.MouseMove(new Point(from.X, from.Y + 4));
        dialog.MouseMove(new Point(from.X, from.Y + 20));
        Assert.True(dialog.IsDraggingRow);
        var target = Center(rows[2], 0.8);                       // lower half of the last row -> drop at the end
        dialog.MouseMove(target);
        dialog.MouseUp(target, MouseButton.Left);
        await Pump();

        Assert.Equal("b,c,a", Order());
        Assert.False(dialog.IsDraggingRow);

        // a plain click does not reorder
        var rows2 = dialog.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        var p = Center(rows2[1]);
        dialog.MouseDown(p, MouseButton.Left);
        dialog.MouseUp(p, MouseButton.Left);
        Assert.Equal("b,c,a", Order());
        dialog.Close();
        await dialog.Model.DisposeAllAsync();
    }
}
