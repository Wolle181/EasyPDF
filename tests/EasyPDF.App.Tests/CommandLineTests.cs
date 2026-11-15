using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Threading;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>PDFs given on the command line: one is opened, several go to the Concatenate dialog.</summary>
public class CommandLineTests
{
    private static (MainViewModel Vm, FakeDialogs Dialogs) Make()
    {
        var dialogs = new FakeDialogs();
        return (new MainViewModel(new AppSettings(), dialogs), dialogs);
    }

    [Fact]
    public async Task ASinglePdfIsOpenedInReadingMode()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        await vm.OpenStartupFilesAsync([dir.Pdf("a.pdf", 3)]);
        Assert.Equal(dir.File("a.pdf"), vm.Document!.FilePath);
        Assert.False(vm.IsOrganizeMode);
        Assert.Null(dialogs.LastConcatContext);
        await vm.Document.DisposeAsync();
    }

    [Fact]
    public async Task SeveralPdfsOpenTheConcatenateDialogWithAllFilesInTheGivenOrder()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        string[] files = [dir.Pdf("c.pdf", 1), dir.Pdf("a.pdf", 2), dir.Pdf("b.pdf", 3)];
        await vm.OpenStartupFilesAsync(files);
        Assert.Equal(files, dialogs.LastConcatContext!.InitialFiles);
        Assert.False(dialogs.LastConcatContext.HasCurrentDocument);
        Assert.Null(vm.Document);                              // dialog cancelled (fake answer null): nothing opened
    }

    [Fact]
    public async Task RelativePathsAreResolvedAndMissingFilesReported()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        var a = dir.Pdf("a.pdf", 2);
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir.Path);
        try
        {
            await vm.OpenStartupFilesAsync(["a.pdf", "missing.pdf"]);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
        Assert.Single(dialogs.Errors);                         // the missing file is reported ...
        Assert.Equal(a, vm.Document!.FilePath);                // ... and the one that exists is opened
        await vm.Document.DisposeAsync();
    }

    [Fact]
    public void ArgumentsAreFilesAndTheMergeOption()
    {
        Assert.Empty(StartupArguments.Parse([]).Files);
        var plain = StartupArguments.Parse(["a.pdf", " ", "b.pdf"]);
        Assert.Equal(new[] { "a.pdf", "b.pdf" }, plain.Files);
        Assert.False(plain.Merge);
        var merge = StartupArguments.Parse(["--MERGE", "x.pdf"]);
        Assert.True(merge.Merge);
        Assert.False(merge.Read);
        Assert.Equal(new[] { "x.pdf" }, merge.Files);
        var read = StartupArguments.Parse(["--read", "x.pdf", "y.pdf"]);
        Assert.True(read.Read);
        Assert.False(read.Merge);
        Assert.Equal(new[] { "x.pdf", "y.pdf" }, read.Files);
    }

    [Fact]
    public async Task MergeWithASingleFileStillShowsTheConcatenateDialog()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = Make();
        await vm.OpenStartupFilesAsync([dir.Pdf("a.pdf", 1)], merge: true);
        Assert.Equal(new[] { dir.File("a.pdf") }, dialogs.LastConcatContext!.InitialFiles);
        Assert.Null(vm.Document);
    }

    [Fact]
    public async Task MergeProcessesHandTheirFilesToTheFirstOne()
    {
        // Explorer starts one process per selected file; here threads play the processes (named mutex and pipe behave the same)
        var name = "EasyPDF.Merge.Test." + System.Guid.NewGuid().ToString("N");
        var quiet = System.TimeSpan.FromSeconds(1);
        var collector = Task.Factory.StartNew(() => MergeInstances.Collect(["/x/c.pdf"], name, quiet), TaskCreationOptions.LongRunning);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var others = new[] { "/x/b.pdf", "/x/a.pdf" }
            .Select(f => Task.Factory.StartNew(() => MergeInstances.Collect([f], name, quiet), TaskCreationOptions.LongRunning)).ToArray();

        foreach (var other in others)
            Assert.Null(await other);                          // handed over: these processes would exit
        Assert.Equal(new[] { "/x/a.pdf", "/x/b.pdf", "/x/c.pdf" }, await collector); // all of them, sorted by name

        // Alone (nothing else selected): the own files come back
        Assert.Equal(new[] { "/x/d.pdf" }, MergeInstances.Collect(["/x/d.pdf"], name, System.TimeSpan.FromMilliseconds(100)));
    }

    [AvaloniaFact]
    public async Task StartingTheMainWindowWithTwoPdfsShowsTheConcatenateDialogAndCombinesThem()
    {
        using var dir = new TempDir();
        var window = new EasyPDF.App.Views.MainWindow { Width = 1000, Height = 700 };
        window.Show();
        var running = window.ViewModel.OpenStartupFilesAsync([dir.Pdf("b.pdf", 2), dir.Pdf("a.pdf", 3)]);
        ConcatenateDialog? dialog = null;
        for (var i = 0; i < 100 && (dialog == null || dialog.Model.Items.Count < 2); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
            dialog = window.OwnedWindows.OfType<ConcatenateDialog>().FirstOrDefault();
        }
        Assert.NotNull(dialog);
        Assert.Equal(new[] { "b.pdf", "a.pdf" }, dialog!.Model.Items.Select(i => i.Name).ToArray());

        // Click OK
        var ok = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.IsDefault);
        var p = ok.TranslatePoint(new Avalonia.Point(ok.Bounds.Width / 2, ok.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseDown(p, Avalonia.Input.MouseButton.Left);
        dialog.MouseUp(p, Avalonia.Input.MouseButton.Left);
        await running;
        Assert.Equal(5, window.ViewModel.Document!.PageCount);  // b (2 pages) + a (3 pages), unsaved
        Assert.False(window.ViewModel.Document.HasFile);
        Assert.All(window.ViewModel.Document.Sources, src => Assert.True(src.IsOpen)); // the files stay open for the new document
        window.Close();
        await window.ViewModel.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task TheConcatenateDialogListsTheStartFilesAndAllowsReordering()
    {
        using var dir = new TempDir();
        var dialog = new ConcatenateDialog(new ConcatenateContext(false, [dir.Pdf("c.pdf", 1), dir.Pdf("a.pdf", 2), dir.Pdf("b.pdf", 3)]), 0);
        dialog.Show();
        for (var i = 0; i < 50 && dialog.Model.Items.Count < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        string Order() => string.Join(",", dialog.Model.Items.Select(i => i.Name[0]));
        Assert.Equal("c,a,b", Order());                         // command line order
        Assert.False(dialog.Model.IncludeCurrent);
        Assert.True(dialog.Model.IsValid);

        dialog.Model.MoveTo(dialog.Model.Items[0], 3);          // the user puts c last
        Assert.Equal("a,b,c", Order());
        Assert.Equal(6, dialog.Model.TotalPages);
        dialog.Close();                                         // [X] = cancel: the opened files are released
    }
}
