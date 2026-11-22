using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Web version: Replace pages offers the documents of the other browser tabs (like other windows on the desktop).</summary>
public class ReplacePagesTabsTests
{
    private static async Task Pump(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task AnotherTabsDocumentIsOfferedPreselectedAndCopiedOnOk()
    {
        using var dir = new TempDir();
        var tabs = new FakeBrowserTabs((new TabDocumentInfo("t1", "b.pdf", 3, true), dir.Pdf("copy-b.pdf", 3)));
        await using var vm = new ReplacePagesDialogViewModel(new ReplacePagesContext(4, 0, [], OtherTabs: tabs));
        Assert.False(vm.HasOpenDocuments);                      // known only once the tabs answered
        await vm.FindOtherTabsAsync();
        Assert.True(vm.HasOpenDocuments);
        Assert.True(vm.UseOpenDocument);
        Assert.Equal(Loc.F("ReplaceDocumentEntry", "b.pdf", Loc.Pages(3)), vm.SelectedOpenDocument!.Label);
        Assert.Equal(0, tabs.Copies);                            // copied only on OK

        var result = await vm.BuildResultAsync();
        Assert.Equal(1, tabs.Copies);
        Assert.Equal(3, result!.Replacement.PageCount);
        Assert.Equal([0, 1, 2], result.Pages);
        await result.Replacement.DisposeAsync();
    }

    [Fact]
    public async Task AChosenFileStaysChosenWhenTheTabsAnswerLater()
    {
        using var dir = new TempDir();
        var tabs = new FakeBrowserTabs((new TabDocumentInfo("t1", "b.pdf", 3, false), dir.Pdf("copy-b.pdf", 3)));
        await using var vm = new ReplacePagesDialogViewModel(new ReplacePagesContext(4, 0, [], OtherTabs: tabs));
        await vm.SetFileAsync(dir.Pdf("c.pdf", 2));
        await vm.FindOtherTabsAsync();
        Assert.True(vm.HasOpenDocuments);
        Assert.True(vm.UseFile);
    }

    [Fact]
    public async Task ATabThatDoesNotHandItsDocumentOverOrChangedItGivesAnError()
    {
        using var dir = new TempDir();
        var tabs = new FakeBrowserTabs((new TabDocumentInfo("gone", "b.pdf", 3, false), null),
            (new TabDocumentInfo("shorter", "c.pdf", 5, false), dir.Pdf("copy-c.pdf", 2)));
        await using var vm = new ReplacePagesDialogViewModel(new ReplacePagesContext(4, 0, [], OtherTabs: tabs));
        await vm.FindOtherTabsAsync();

        Assert.Null(await vm.BuildResultAsync());
        Assert.Equal(Loc.T("TabCopyFailed"), vm.SourceErrorText);

        vm.SelectedOpenDocument = vm.OpenDocuments[1];
        vm.IsInsertRange = true;
        vm.InsertRangeText = "4-5";                              // the tab said 5 pages, its copy has 2
        Assert.True(vm.IsValid);
        Assert.Null(await vm.BuildResultAsync());
        Assert.Equal(Loc.T("TabDocumentChanged"), vm.SourceErrorText);

        vm.InsertAllPages = true;                                 // all pages: all of the copy
        var result = await vm.BuildResultAsync();
        Assert.Equal([0, 1], result!.Pages);
        await result.Replacement.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TheOverlayListsTheOtherTabsDocumentOnceShown()
    {
        using var dir = new TempDir();
        var tabs = new FakeBrowserTabs((new TabDocumentInfo("t1", "b.pdf", 2, false), dir.Pdf("copy-b.pdf", 2)));
        var window = new MainWindow { Width = 1200, Height = 800 };
        window.Show();
        await Pump();
        var layer = window.FindControl<DialogLayer>("Dialogs")!;
        var view = new ReplacePagesDialogView(new ReplacePagesContext(4, 0, [], OtherTabs: tabs));
        _ = layer.ShowAsync(view);
        await Pump();

        var box = view.FindControl<ComboBox>("DocumentBox")!;
        Assert.True(box.IsVisible);
        Assert.Equal("b.pdf", Assert.IsType<ReplaceDocumentItem>(box.SelectedItem).Name);
        Assert.True(view.FindControl<Button>("OkButton")!.IsEnabled);
        window.Close();
    }
}
