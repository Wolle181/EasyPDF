using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EasyPDF.App.Views;
using Xunit;

namespace EasyPDF.App.Tests;

public class BlankPageUiTests
{
    [AvaloniaFact]
    public async Task BlankPageCommandsInsertAnEmptyPageOfTheNeighboursSize()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        for (var i = 0; i < 15; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        var vm = window.ViewModel;

        await vm.InsertBlankAfterCommand.ExecuteAsync(1); // after the second page (110 x 200)
        Assert.Equal(4, vm.Document!.PageCount);
        Assert.Equal(110, vm.Document.Pages[2].DisplaySize.Width, 1);
        Assert.Equal(200, vm.Document.Pages[2].DisplaySize.Height, 1);

        await vm.InsertBlankBeforeCommand.ExecuteAsync(0); // before the first page (100 x 200)
        Assert.Equal(5, vm.Document.PageCount);
        Assert.Equal(100, vm.Document.Pages[0].DisplaySize.Width, 1);

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.Equal(3, vm.Document.PageCount);
        window.Close();
        await vm.Document.DisposeAsync();
    }
}
