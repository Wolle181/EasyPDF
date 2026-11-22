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
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Web version: the Concatenate dialog adds the documents of the other browser tabs (like other windows on the desktop).</summary>
public class ConcatenateTabsTests
{
    /// <summary>Other tabs: each document's copy is a file, or null for a tab that does not hand it over.</summary>
    private sealed class FakeTabs(params (TabDocumentInfo Document, string? Copy)[] tabs) : IBrowserTabs
    {
        public int Copies { get; private set; }

        public void ShareDocument(Func<OpenDocumentInfo?> current) { }

        public Task<IReadOnlyList<TabDocumentInfo>> OtherDocumentsAsync() =>
            Task.FromResult<IReadOnlyList<TabDocumentInfo>>([.. tabs.Select(t => t.Document)]);

        public Task<string?> CopyDocumentAsync(TabDocumentInfo document)
        {
            Copies++;
            return Task.FromResult(tabs.Single(t => t.Document == document).Copy);
        }
    }

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
    public async Task TheDocumentsOfOtherTabsAreAddedOnceAndAFailedOneIsMarked()
    {
        using var dir = new TempDir();
        var tabs = new FakeTabs((new TabDocumentInfo("t1", "b.pdf", 3, true), dir.Pdf("copy-b.pdf", 3)),
            (new TabDocumentInfo("t2", "c.pdf", 2, false), null));
        var vm = new ConcatenateDialogViewModel(new ConcatenateContext(true, OtherTabs: tabs), 1);
        Assert.False(vm.HasOtherDocuments);                          // known only once the tabs answered
        await vm.FindOtherTabsAsync();
        Assert.True(vm.HasOtherDocuments);

        await vm.AddAllOpenDocumentsCommand.ExecuteAsync(null);
        await vm.AddAllOpenDocumentsCommand.ExecuteAsync(null);      // already listed: not copied again
        Assert.Equal(2, tabs.Copies);
        Assert.Equal(new[] { Loc.T("ConcatCurrentDocument"), "b.pdf", "c.pdf" }, vm.Items.Select(i => i.Name).ToArray());
        Assert.Equal(Loc.T("ConcatFromTabEdited"), vm.Items[1].Folder);
        Assert.Equal(3, vm.Items[1].PageCount);
        Assert.Equal(Loc.T("ConcatTabFailed"), vm.Items[2].Error);
        Assert.False(vm.IsValid);

        vm.Remove(vm.Items[2]);
        Assert.True(vm.IsValid);
        var result = vm.BuildResult()!;
        Assert.Null(result.Parts[0]);
        Assert.Equal(3, result.Parts[1]!.PageCount);
        await result.Parts[1]!.DisposeAsync();
    }

    [Fact]
    public async Task WithoutDocumentsInOtherTabsTheButtonStaysHidden()
    {
        var vm = new ConcatenateDialogViewModel(new ConcatenateContext(true, OtherTabs: new FakeTabs()), 1);
        await vm.FindOtherTabsAsync();
        Assert.False(vm.HasOtherDocuments);
    }

    [AvaloniaFact]
    public async Task TheOverlayShowsTheButtonWhenOtherTabsHaveADocumentAndAddsItOnClick()
    {
        using var dir = new TempDir();
        var tabs = new FakeTabs((new TabDocumentInfo("t1", "b.pdf", 2, false), dir.Pdf("copy-b.pdf", 2)));
        var window = new MainWindow { Width = 1200, Height = 800 };
        window.Show();
        await Pump();
        var layer = window.FindControl<DialogLayer>("Dialogs")!;
        var view = new ConcatenateDialogView(new ConcatenateContext(false, OtherTabs: tabs), 0);
        _ = layer.ShowAsync(view);
        await Pump();

        var button = view.FindControl<Button>("OpenDocumentsButton")!;
        Assert.True(button.IsVisible);
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        await Pump();

        var item = Assert.Single(view.Model.Items);
        Assert.Equal("b.pdf", item.Name);
        Assert.Equal(Loc.T("ConcatFromTab"), item.Folder);
        await view.Model.DisposeAllAsync();
        window.Close();
    }
}
