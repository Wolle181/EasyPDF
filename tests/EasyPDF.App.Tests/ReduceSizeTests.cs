using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>"Reduce file size": level dialog, save under a new name, report how much smaller the file got.</summary>
public class ReduceSizeTests
{
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
    public async Task ReducingSavesASmallerCopyShowsItAndTellsHowMuchSmallerItIs()
    {
        using var dir = new TempDir();
        var original = dir.ScanPdf("scan.pdf");
        var before = File.ReadAllBytes(original);
        var dialogs = new FakeDialogs { ReduceAnswer = ImageReduction.Small, SavePath = dir.File("small.pdf") };
        var vm = new MainViewModel(new AppSettings(), dialogs);
        await vm.OpenPathAsync(original);

        await vm.ReduceSizeCommand.ExecuteAsync(null);
        Assert.Equal(ImageReduction.Medium, dialogs.LastReducePreset);             // the default level
        Assert.Equal(Loc.F("ReduceFileNameSmall", "scan"), dialogs.SuggestedFileName); // the name tells the level
        Assert.Equal(dir.File("small.pdf"), vm.Document!.FilePath);                // the window shows the smaller file
        Assert.False(vm.IsDirty);
        Assert.Equal(before, File.ReadAllBytes(original));                          // the original stays as it is
        var after = new FileInfo(dir.File("small.pdf")).Length;
        Assert.True(after < before.Length / 4);
        var info = Assert.Single(dialogs.Infos);
        Assert.Contains(" → ", info);                                                 // old and new size
        Assert.Empty(dialogs.Errors);

        // The level chosen last is offered next time; a reduced file has nothing left to reduce
        dialogs.SavePath = dir.File("smaller.pdf");
        dialogs.ReduceAnswer = ImageReduction.High;
        await vm.ReduceSizeCommand.ExecuteAsync(null);
        Assert.Equal(ImageReduction.Small, dialogs.LastReducePreset);
        Assert.Equal(Loc.F("ReduceFileNameHigh", "small"), dialogs.SuggestedFileName);
        Assert.Equal(Loc.T("MsgReducedNothing"), dialogs.Infos[^1]);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task CancellingTheDialogWritesNothing()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs { ReduceAnswer = null, SavePath = dir.File("small.pdf") };
        var vm = new MainViewModel(new AppSettings(), dialogs);
        await vm.OpenPathAsync(dir.ScanPdf("scan.pdf"));
        await vm.ReduceSizeCommand.ExecuteAsync(null);
        Assert.False(File.Exists(dir.File("small.pdf")));
        Assert.Empty(dialogs.Infos);
        Assert.Equal(dir.File("scan.pdf"), vm.Document!.FilePath);
        await vm.Document.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TheDialogPreselectsTheLastLevelAndReturnsTheChosenOne()
    {
        var owner = new Window { Width = 600, Height = 500 };
        owner.Show();

        var dialog = new ReduceSizeDialog(ImageReduction.High);
        var result = dialog.ShowDialog<ImageReduction?>(owner);
        await Pump();
        Assert.True(dialog.FindControl<RadioButton>("HighOption")!.IsChecked);
        var small = dialog.FindControl<RadioButton>("SmallOption")!;
        dialog.MouseDown(CenterOf(small, dialog), MouseButton.Left);
        dialog.MouseUp(CenterOf(small, dialog), MouseButton.Left);
        var ok = dialog.FindControl<Button>("OkButton")!;
        dialog.MouseDown(CenterOf(ok, dialog), MouseButton.Left);
        dialog.MouseUp(CenterOf(ok, dialog), MouseButton.Left);
        Assert.Equal(ImageReduction.Small, await result);

        var closed = new ReduceSizeDialog(ImageReduction.Medium);
        var none = closed.ShowDialog<ImageReduction?>(owner);
        await Pump();
        Assert.True(closed.FindControl<RadioButton>("MediumOption")!.IsChecked);
        closed.Close(); // [X] = cancel
        Assert.Null(await none);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TheRibbonOffersTheCommandOnceADocumentIsOpen()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        var buttons = window.LogicalDescendantsOfType<RibbonButton>().Where(b => b.Command == window.ViewModel.ReduceSizeCommand).ToList();
        Assert.NotEmpty(buttons);
        Assert.False(window.ViewModel.ReduceSizeCommand.CanExecute(null));

        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        Assert.True(window.ViewModel.ReduceSizeCommand.CanExecute(null));
        window.Close();
        await window.ViewModel.Document!.DisposeAsync();
    }
}

internal static class LogicalTreeHelper
{
    /// <summary>All controls of a type below <paramref name="root"/>, also on ribbon tabs that are not shown (logical tree).</summary>
    public static System.Collections.Generic.IEnumerable<T> LogicalDescendantsOfType<T>(this Control root) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<T>();
}
