using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Several documents at once: one per window, a window with a document opens further files in a new one.</summary>
public class MultiWindowTests
{
    private static MainWindow ShowFirst(WindowManager windows)
    {
        var window = windows.CreateWindow();
        window.Width = 1000;
        window.Height = 700;
        window.Show();
        return window;
    }

    private static async Task CloseAllAsync(WindowManager windows)
    {
        var documents = windows.Windows.Select(w => w.ViewModel.Document).OfType<EditableDocument>().ToList();
        foreach (var window in windows.Windows.ToList())
            window.CloseWindow();
        foreach (var document in documents)
            await document.DisposeAsync();
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> find) where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (find() is { } found)
                return found;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException();
    }

    [AvaloniaFact]
    public async Task AFileOpensInTheEmptyWindowAndFurtherFilesInNewWindows()
    {
        using var dir = new TempDir();
        var windows = new WindowManager(new AppSettings());
        var first = ShowFirst(windows);
        await windows.OpenFilesAsync([dir.Pdf("a.pdf", 2)], merge: false);
        Assert.Single(windows.Windows);
        Assert.Equal(dir.File("a.pdf"), first.ViewModel.Document!.FilePath);

        await first.ViewModel.OpenPathAsync(dir.Pdf("b.pdf", 3));     // File ▸ Open in a window with a document
        Assert.Equal(2, windows.Count);
        var second = windows.Windows.Single(w => w != first);
        Assert.True(second.IsVisible);
        Assert.Equal(dir.File("b.pdf"), second.ViewModel.Document!.FilePath);
        Assert.Equal(dir.File("a.pdf"), first.ViewModel.Document.FilePath); // the first window keeps its document

        // A file that already is the document of a window is not opened a second time
        await second.ViewModel.OpenPathAsync(dir.File("a.pdf"));
        await windows.OpenFilesAsync([dir.File("b.pdf")], merge: false);
        Assert.Equal(2, windows.Count);

        // From outside, a new file starts at the active window and gets a window of its own
        await windows.OpenFilesAsync([dir.Pdf("c.pdf", 1)], merge: false);
        Assert.Equal(3, windows.Count);
        Assert.Contains(windows.Windows, w => w.ViewModel.Document?.FilePath == dir.File("c.pdf"));
        await CloseAllAsync(windows);
    }

    [AvaloniaFact]
    public async Task ReadOnSeveralFilesOpensEachInAWindowOfItsOwn()
    {
        using var dir = new TempDir();
        var windows = new WindowManager(new AppSettings());
        var first = ShowFirst(windows);
        await windows.OpenEachAsync([dir.Pdf("a.pdf", 1), dir.Pdf("b.pdf", 2), dir.Pdf("c.pdf", 3)]);
        Assert.Equal(3, windows.Count);
        Assert.Equal(dir.File("a.pdf"), first.ViewModel.Document!.FilePath);      // the empty window takes the first one
        Assert.Equal(
            new[] { "a.pdf", "b.pdf", "c.pdf" },
            windows.Windows.Select(w => Path.GetFileName(w.ViewModel.Document!.FilePath)).Order());
        Assert.All(windows.Windows, w => Assert.Empty(w.OwnedWindows));        // no Concatenate dialog
        await CloseAllAsync(windows);
    }

    [AvaloniaFact]
    public async Task RequestsArrivingAtOnceDoNotShareTheEmptyWindow()
    {
        // Explorer starts one process per selected file; their requests reach the running instance almost together
        using var dir = new TempDir();
        var windows = new WindowManager(new AppSettings());
        var first = ShowFirst(windows);
        var requests = new[] { "a.pdf", "b.pdf", "c.pdf" }
            .Select((name, i) => windows.OpenFilesAsync([dir.Pdf(name, i + 1)], merge: false))
            .ToList();
        await Task.WhenAll(requests);
        Assert.Equal(3, windows.Count);
        Assert.Equal(
            new[] { "a.pdf", "b.pdf", "c.pdf" },
            windows.Windows.Select(w => Path.GetFileName(w.ViewModel.Document?.FilePath)).Order());
        Assert.Equal(dir.File("a.pdf"), first.ViewModel.Document!.FilePath);
        await CloseAllAsync(windows);
    }

    [AvaloniaFact]
    public async Task AStartWithoutFilesReusesAnEmptyWindowOrOpensOne()
    {
        using var dir = new TempDir();
        var windows = new WindowManager(new AppSettings());
        var first = ShowFirst(windows);
        await windows.OpenFilesAsync([], merge: false);
        Assert.Single(windows.Windows);                                // the empty window is shown

        await first.ViewModel.OpenPathAsync(dir.Pdf("a.pdf", 1));
        await windows.OpenFilesAsync([], merge: false);
        Assert.Equal(2, windows.Count);                                 // all windows have a document: a new one
        await windows.OpenFilesAsync([], merge: false);
        Assert.Equal(2, windows.Count);                                 // ... and that one is reused
        await CloseAllAsync(windows);
    }

    [AvaloniaFact]
    public async Task TheShortcutOpensANewEmptyWindowOfTheSameSize()
    {
        var windows = new WindowManager(new AppSettings());
        var first = ShowFirst(windows);
        first.KeyPress(Key.N, RawInputModifiers.Control, PhysicalKey.N, "n");
        var second = await WaitForAsync(() => windows.Windows.FirstOrDefault(w => w != first));
        Assert.True(second.IsVisible);
        Assert.False(second.ViewModel.HasDocument);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        await CloseAllAsync(windows);
    }

    [AvaloniaFact]
    public async Task SeveralFilesFromOutsideGetTheConcatenateDialogInANewWindow()
    {
        using var dir = new TempDir();
        var windows = new WindowManager(new AppSettings());
        var first = ShowFirst(windows);
        await first.ViewModel.OpenPathAsync(dir.Pdf("a.pdf", 1));

        var running = windows.OpenFilesAsync([dir.Pdf("b.pdf", 2), dir.Pdf("c.pdf", 3)], merge: true);
        var second = await WaitForAsync(() => windows.Windows.FirstOrDefault(w => w != first));
        var dialog = await WaitForAsync(() => second.OwnedWindows.OfType<ConcatenateDialog>().FirstOrDefault(d => d.Model.Items.Count == 2));
        Assert.False(dialog.Model.IncludeCurrent);                     // the new window has no document to offer
        dialog.Close();                                                 // [X] = cancel
        await running;
        Assert.Equal(dir.File("a.pdf"), first.ViewModel.Document!.FilePath);
        await CloseAllAsync(windows);
    }

    [AvaloniaFact]
    public async Task ClosingTheDocumentClosesItsWindowWhileOthersAreOpen()
    {
        using var dir = new TempDir();
        var windows = new WindowManager(new AppSettings());
        var first = ShowFirst(windows);
        await first.ViewModel.OpenPathAsync(dir.Pdf("a.pdf", 1));
        await first.ViewModel.OpenPathAsync(dir.Pdf("b.pdf", 1));
        var second = windows.Windows.Single(w => w != first);

        await second.ViewModel.CloseDocumentCommand.ExecuteAsync(null);
        Assert.Single(windows.Windows);
        Assert.False(second.IsVisible);

        await first.ViewModel.CloseDocumentCommand.ExecuteAsync(null);  // the last window stays open, empty
        Assert.Single(windows.Windows);
        Assert.False(first.ViewModel.HasDocument);
        await CloseAllAsync(windows);
    }

    [AvaloniaFact]
    public async Task ExitClosesAllWindowsAndStopsWhenUnsavedChangesAreKept()
    {
        using var dir = new TempDir();
        var windows = new WindowManager(new AppSettings());
        var first = ShowFirst(windows);
        await first.ViewModel.OpenPathAsync(dir.Pdf("a.pdf", 1));
        await first.ViewModel.OpenPathAsync(dir.Pdf("b.pdf", 1));
        first.ViewModel.RotateRightCommand.Execute(null);               // unsaved change in the first window

        var exiting = first.ViewModel.ExitCommand.ExecuteAsync(null);
        var ask = await WaitForAsync(() => first.OwnedWindows.OfType<UnsavedChangesDialog>().FirstOrDefault());
        ask.Close();                                                    // [X] = cancel: the first window stays
        await exiting;
        Assert.Contains(first, windows.Windows);
        Assert.True(first.IsVisible);

        var document = first.ViewModel.Document!;
        first.ViewModel.UndoCommand.Execute(null);
        await first.ViewModel.ExitCommand.ExecuteAsync(null);
        Assert.Empty(windows.Windows);
        await document.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task RecentFilesShowInEveryWindow()
    {
        using var dir = new TempDir();
        var windows = new WindowManager(new AppSettings());
        var first = ShowFirst(windows);
        await first.ViewModel.OpenPathAsync(dir.Pdf("a.pdf", 1));
        await first.ViewModel.OpenPathAsync(dir.Pdf("b.pdf", 1));      // opened in the second window
        var recent = (MenuFlyout)first.FindControl<RibbonButton>("RecentButton")!.Flyout!;
        Assert.Equal(new[] { "b.pdf", "a.pdf" }, recent.Items.OfType<MenuItem>().Where(i => i.Command == first.ViewModel.OpenRecentCommand).Select(i => i.Header).ToArray());
        Assert.Same(first.ViewModel.ClearRecentCommand, recent.Items.OfType<MenuItem>().Last().Command);        // "Clear list" at the end
        await CloseAllAsync(windows);
    }

    [Fact]
    public async Task SavingOntoAFileAnotherWindowReadsIsRefused()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var others = new FakeWindows { UsedElsewhere = dir.Pdf("b.pdf", 1) };
        var vm = new MainViewModel(new AppSettings(), dialogs, windows: others);
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 2));
        var before = File.ReadAllBytes(dir.File("b.pdf"));
        dialogs.SavePath = dir.File("b.pdf");
        await vm.SaveAsCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Errors);
        Assert.Equal(before, File.ReadAllBytes(dir.File("b.pdf")));
        Assert.Equal(dir.File("a.pdf"), vm.Document!.FilePath);
        await vm.Document.DisposeAsync();
    }

    [Fact]
    public async Task ConcatenatingWithoutTheCurrentDocumentOpensANewWindow()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var others = new FakeWindows();
        var vm = new MainViewModel(new AppSettings(), dialogs, windows: others);
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 1));
        var b = await PdfSource.OpenAsync(dir.Pdf("b.pdf", 2), token: TestContext.Current.CancellationToken);
        var c = await PdfSource.OpenAsync(dir.Pdf("c.pdf", 3), token: TestContext.Current.CancellationToken);
        dialogs.ConcatAnswer = new ConcatenateResult([EditableDocument.FromSource(b), EditableDocument.FromSource(c)]);
        await vm.ConcatenateCommand.ExecuteAsync(null);
        Assert.Equal(0, dialogs.UnsavedAsked);
        Assert.Equal(dir.File("a.pdf"), vm.Document!.FilePath);       // this window keeps its document
        var combined = others.Opened.Single().Document!;
        Assert.Equal(5, combined.PageCount);
        Assert.False(combined.HasFile);
        await vm.Document.DisposeAsync();
        await combined.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TheConcatenateDialogOffersTheDocumentsOfTheOtherWindowsWithTheirEdits()
    {
        using var dir = new TempDir();
        var windows = new WindowManager(new AppSettings());
        var first = ShowFirst(windows);
        await first.ViewModel.OpenPathAsync(dir.Pdf("a.pdf", 1));
        await first.ViewModel.OpenPathAsync(dir.Pdf("b.pdf", 3));         // second window
        await first.ViewModel.OpenPathAsync(dir.Pdf("c.pdf", 2));         // third window
        var second = windows.Windows.Single(w => w.ViewModel.Document?.FilePath == dir.File("b.pdf"));
        second.ViewModel.DeletePagesCommand.Execute(0);                   // unsaved edit: pages 2 and 3 left

        var running = first.ViewModel.ConcatenateCommand.ExecuteAsync(null);
        var dialog = await WaitForAsync(() => first.OwnedWindows.OfType<ConcatenateDialog>().FirstOrDefault());
        Assert.True(dialog.Model.IncludeCurrent);
        var button = dialog.FindControl<Button>("OpenDocumentsButton")!;
        Assert.True(button.IsVisible);
        void ClickButton()
        {
            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(center, MouseButton.Left);
            dialog.MouseUp(center, MouseButton.Left);
        }

        // One click adds the documents of all other windows right away
        ClickButton();
        await WaitForAsync(() => dialog.Model.Items.Count == 3 ? dialog.Model.Items[2] : null);
        Assert.Equal(new[] { "b.pdf", "c.pdf" }, dialog.Model.Items.Skip(1).Select(i => i.Name).Order().ToArray());
        var entryB = dialog.Model.Items.Single(i => i.Name == "b.pdf");
        Assert.Equal(Loc.T("ConcatFromWindowEdited"), entryB.Folder);
        Assert.Equal(2, entryB.PageCount);
        var entryC = dialog.Model.Items.Single(i => i.Name == "c.pdf");
        Assert.Equal(Loc.T("ConcatFromWindow"), entryC.Folder);

        ClickButton();                                                    // listed ones are not added twice
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, dialog.Model.Items.Count);
        dialog.Model.Remove(entryC);                                      // unwanted ones are removed from the list

        // The entry is a copy: closing the other window afterwards does not take its pages away
        var secondDocument = second.ViewModel.Document!;
        second.CloseWindow();
        await secondDocument.DisposeAsync();
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await running;

        var combined = first.ViewModel.Document!;
        Assert.False(combined.HasFile);
        Assert.Equal(new[] { "a.pdf", "b.pdf", "b.pdf" }, combined.Pages.Select(p => p.Source.FileName).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, combined.Pages.Select(p => p.SourceIndex).ToArray());
        await combined.SaveAsync(dir.File("all.pdf"));
        var saved = await PdfSource.OpenAsync(dir.File("all.pdf"), token: TestContext.Current.CancellationToken);
        Assert.Equal(3, saved.PageCount);
        await saved.DisposeAsync();
        await CloseAllAsync(windows);
    }

    [Fact]
    public void LaterStartsHandTheirFilesToTheRunningInstance()
    {
        var name = "EasyPDF.Instance.Test." + Guid.NewGuid().ToString("N");
        using var channel = InstanceChannel.Start(name, StartupArguments.Parse([]), out var handedOver);
        Assert.NotNull(channel);
        Assert.False(handedOver);

        var received = new List<StartupArguments>();
        using var arrived = new SemaphoreSlim(0);
        // Before the UI is ready: the request waits for the handler
        Assert.Null(InstanceChannel.Start(name, StartupArguments.Parse(["--merge", "/x/a.pdf", "/x/b.pdf"]), out handedOver));
        Assert.True(handedOver);
        channel!.SetHandler(r =>
        {
            lock (received)
                received.Add(r);
            arrived.Release();
        });
        Assert.Null(InstanceChannel.Start(name, StartupArguments.Parse([]), out handedOver));   // a start without files
        Assert.True(handedOver);
        Assert.Null(InstanceChannel.Start(name, StartupArguments.Parse(["--read", "/x/c.pdf"]), out handedOver));
        Assert.True(handedOver);

        var token = TestContext.Current.CancellationToken;
        Assert.True(arrived.Wait(5000, token) && arrived.Wait(5000, token) && arrived.Wait(5000, token));
        lock (received)
        {
            Assert.True(received[0].Merge);
            Assert.Equal(new[] { "/x/a.pdf", "/x/b.pdf" }, received[0].Files);
            Assert.False(received[1].Merge);
            Assert.Empty(received[1].Files);
            Assert.True(received[2].Read);
            Assert.False(received[2].Merge);
            Assert.Equal(new[] { "/x/c.pdf" }, received[2].Files);
        }
    }

    /// <summary>Other windows for view model tests: new windows are plain view models.</summary>
    private sealed class FakeWindows : IDocumentWindows
    {
        public string? UsedElsewhere;
        public readonly List<MainViewModel> Opened = [];

        public int Count => 1 + Opened.Count;

        public AreaClipboard AreaClipboard { get; } = new();

        public void OpenNewWindow() => Opened.Add(new MainViewModel(new AppSettings(), new FakeDialogs()));

        public bool ActivateWindowShowing(string path) => false;

        public bool IsFileUsedByOtherWindow(string path, MainViewModel asking) =>
            UsedElsewhere != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(UsedElsewhere), StringComparison.OrdinalIgnoreCase);

        public async Task OpenInNewWindowAsync(MainViewModel from, Func<MainViewModel, Task> load)
        {
            var vm = new MainViewModel(new AppSettings(), new FakeDialogs());
            Opened.Add(vm);
            await load(vm);
        }

        public Task CloseAllAsync() => Task.CompletedTask;

        public IReadOnlyList<OpenDocumentInfo> OtherDocuments(MainViewModel asking) => [];
    }
}
