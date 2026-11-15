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
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Returns two fixed words per page (pixel boxes); optionally blocks until cancelled.</summary>
internal sealed class FakeOcrEngine : IOcrEngine
{
    public bool BlockUntilCancelled;
    public int Calls;

    public IReadOnlyList<OcrWord> Recognize(RenderedPage image, CancellationToken token = default)
    {
        Interlocked.Increment(ref Calls);
        if (BlockUntilCancelled)
        {
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
        }
        return [new OcrWord("Hallo", new RectPt(20, 20, 120, 40), 0.9f), new OcrWord("Welt", new RectPt(160, 20, 100, 40), 0.9f)];
    }

    public void Dispose()
    {
    }
}

public class OcrDialogViewModelTests
{
    [Fact]
    public void AllPagesAndSkippingAreTheDefaults()
    {
        var vm = new OcrDialogViewModel(new OcrDialogContext(5, 2, []));
        Assert.True(vm.IsAll);
        Assert.False(vm.CanSelectSelected);
        Assert.Equal("Current page (3)", vm.CurrentPageText);
        var request = vm.BuildRequest()!;
        Assert.Equal([0, 1, 2, 3, 4], request.Pages);
        Assert.True(request.SkipPagesWithText);
    }

    [Fact]
    public void SelectionIsPreselectedAndRangesAreChecked()
    {
        var vm = new OcrDialogViewModel(new OcrDialogContext(5, 0, [1, 3]));
        Assert.True(vm.IsSelected);
        Assert.Equal([1, 3], vm.BuildRequest()!.Pages);

        vm.IsRangeScope = true;
        vm.RangeText = "2-9";
        Assert.False(vm.IsValid);
        Assert.NotEqual("", vm.ErrorText);
        Assert.Null(vm.BuildRequest());
        vm.RangeText = "2-3, 5";
        vm.SkipPagesWithText = false;
        Assert.Equal([1, 2, 4], vm.BuildRequest()!.Pages);
        Assert.False(vm.BuildRequest()!.SkipPagesWithText);

        vm.IsCurrent = true;
        Assert.Equal([0], vm.BuildRequest()!.Pages);
    }
}

public class OcrCommandTests
{
    private static (MainViewModel Vm, FakeDialogs Dialogs, FakeOcrEngine Engine) Make()
    {
        var dialogs = new FakeDialogs();
        var engine = new FakeOcrEngine();
        return (new MainViewModel(new AppSettings(), dialogs, null, () => engine), dialogs, engine);
    }

    [Fact]
    public async Task OneRecognizedPageIsCountedInTheSingular()
    {
        using var dir = new TempDir();
        var (vm, dialogs, _) = Make();
        await vm.OpenPathAsync(dir.Pdf("scan.pdf", 3));
        dialogs.OcrAnswer = new OcrRequest([0], SkipPagesWithText: true);

        await vm.RecognizeTextCommand.ExecuteAsync(null);
        Assert.Equal("Unsaved changes (text layer on 1 page)", vm.StatusDirtyText);
        Assert.Equal("Text recognized on 1 page (2 words). Save the document to keep the text layer.", vm.StatusMessage);

        var old = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de");
        try
        {
            Assert.Equal("Nicht gespeicherte Änderungen (Textebene auf 1 Seite)", vm.StatusDirtyText);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = old;
        }
    }

    [Fact]
    public async Task RecognizedTextIsOneUndoStepAndIsSavedAsTextLayer()
    {
        using var dir = new TempDir();
        var (vm, dialogs, engine) = Make();
        await vm.OpenPathAsync(dir.Pdf("scan.pdf", 3));
        dialogs.OcrAnswer = new OcrRequest([0, 2], SkipPagesWithText: true);

        await vm.RecognizeTextCommand.ExecuteAsync(null);
        Assert.Equal(3, dialogs.LastOcrContext!.PageCount);
        Assert.Equal(2, engine.Calls);
        var document = vm.Document!;
        Assert.Equal(2, document.OcrPageCount);
        Assert.Empty(document.Pages[1].OcrWords);
        Assert.Equal("Unsaved changes (text layer on 2 pages)", vm.StatusDirtyText);
        Assert.Equal("Text recognized on 2 pages (4 words). Save the document to keep the text layer.", vm.StatusMessage);
        Assert.False(vm.IsBusy);
        Assert.Empty(dialogs.Errors);

        vm.UndoCommand.Execute(null);
        Assert.Equal(0, document.OcrPageCount);
        vm.RedoCommand.Execute(null);
        Assert.Equal(2, document.OcrPageCount);

        dialogs.SavePath = dir.File("searchable.pdf");
        await vm.SaveAsCommand.ExecuteAsync(null);
        Assert.Equal("", vm.StatusDirtyText);
        await using (var saved = await PdfSource.OpenAsync(dir.File("searchable.pdf"), null, TestContext.Current.CancellationToken))
        {
            Assert.Equal(["Hallo", "Welt"], (await saved.ReadWordsAsync(0, TestContext.Current.CancellationToken)).Select(w => w.Text));
            Assert.Empty(await saved.ReadWordsAsync(1, TestContext.Current.CancellationToken));
        }

        // Again with skipping: the pages have text now, nothing to do
        dialogs.OcrAnswer = new OcrRequest([0, 2], SkipPagesWithText: true);
        await vm.RecognizeTextCommand.ExecuteAsync(null);
        Assert.Equal(2, engine.Calls);
        Assert.Equal("All chosen pages already contain text; nothing was recognized.", vm.StatusMessage);
        Assert.False(vm.IsDirty);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task CopyTextCopiesTheSelectedPagesOnlyAndSaysSoInTheStatusBar()
    {
        using var dir = new TempDir();
        var (vm, dialogs, _) = Make();
        await vm.OpenPathAsync(dir.Pdf("scan.pdf", 3));
        dialogs.OcrAnswer = new OcrRequest([0, 2], SkipPagesWithText: false);
        await vm.RecognizeTextCommand.ExecuteAsync(null);
        var ids = vm.Document!.Pages.Select(p => p.Id).ToArray();

        // Only in Organize mode and only with a selection
        Assert.False(vm.CopyTextCommand.CanExecute(null));
        vm.IsOrganizeMode = true;
        Assert.False(vm.CopyTextCommand.CanExecute(null));

        vm.SetSelection([ids[1]]); // a page without text
        Assert.True(vm.CopyTextCommand.CanExecute(null));
        await vm.CopyTextCommand.ExecuteAsync(null);
        Assert.Null(dialogs.ClipboardText);
        Assert.Equal("The selected pages contain no text. Run \"Recognize text (OCR)\" first.", vm.StatusMessage);

        vm.SetSelection([ids[0], ids[1]]);
        await vm.CopyTextCommand.ExecuteAsync(null);
        Assert.Equal("Hallo Welt", dialogs.ClipboardText);
        Assert.Equal("Text of 1 page(s) copied to the clipboard (10 characters).", vm.StatusMessage);

        vm.SetSelection([ids[2], ids[0]]); // document order, not click order
        await vm.CopyTextCommand.ExecuteAsync(null);
        Assert.Equal("Hallo Welt\n\nHallo Welt", dialogs.ClipboardText);
        Assert.Equal("Text of 2 page(s) copied to the clipboard (22 characters).", vm.StatusMessage);

        // After saving the text comes from the file's text layer
        dialogs.SavePath = dir.File("searchable.pdf");
        await vm.SaveAsCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.Document!.OcrPageCount);
        dialogs.ClipboardText = null;
        vm.SelectAllCommand.Execute(null);
        await vm.CopyTextCommand.ExecuteAsync(null);
        Assert.Equal("Hallo Welt\n\nHallo Welt", dialogs.ClipboardText);
        Assert.Empty(dialogs.Errors);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task CancellingTheDialogDoesNothing()
    {
        using var dir = new TempDir();
        var (vm, dialogs, engine) = Make();
        await vm.OpenPathAsync(dir.Pdf("scan.pdf", 2));
        await vm.RecognizeTextCommand.ExecuteAsync(null); // OcrAnswer = null: the dialog was cancelled
        Assert.NotNull(dialogs.LastOcrContext);
        Assert.Equal(0, engine.Calls);
        Assert.False(vm.IsDirty);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task CancelStopsTheRecognitionAndKeepsTheDocumentUnchanged()
    {
        using var dir = new TempDir();
        var (vm, dialogs, engine) = Make();
        engine.BlockUntilCancelled = true;
        await vm.OpenPathAsync(dir.Pdf("scan.pdf", 3));
        dialogs.OcrAnswer = new OcrRequest([0, 1, 2], SkipPagesWithText: false);

        var running = vm.RecognizeTextCommand.ExecuteAsync(null);
        for (var i = 0; i < 400 && engine.Calls == 0; i++)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(vm.IsBusy);
        Assert.Equal("Recognizing text: page 1 of 3…", vm.BusyText);
        Assert.False(vm.RecognizeTextCommand.CanExecute(null)); // one operation at a time
        vm.CancelOperationCommand.Execute(null);
        await running;

        Assert.False(vm.IsBusy);
        Assert.Empty(dialogs.Errors);
        Assert.Equal("Operation cancelled.", vm.StatusMessage);
        Assert.Equal(0, vm.Document!.OcrPageCount);
        Assert.False(vm.IsDirty);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task MissingModelsAreReported()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new AppSettings(), dialogs, null, () => throw new FileNotFoundException("OCR model not found.", "x.onnx"));
        await vm.OpenPathAsync(dir.Pdf("scan.pdf", 1));
        dialogs.OcrAnswer = new OcrRequest([0], true);
        await vm.RecognizeTextCommand.ExecuteAsync(null);
        Assert.Equal(["The text could not be recognized."], dialogs.Errors);
        Assert.False(vm.IsBusy);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task TheRealEngineRecognizesARenderedPage()
    {
        // End to end with RapidOcrNet and the models next to the app: a page with real text, OCR without skipping
        using var dir = new TempDir();
        var path = dir.Pdf("letter.pdf", 1);
        var source = await PdfSource.OpenAsync(path, null, TestContext.Current.CancellationToken);
        await source.LoadPageSizesAsync(null, TestContext.Current.CancellationToken);
        await using (var d = EditableDocument.FromSource(source))
        {
            d.AddText(0, "Grüße", "Helvetica", 24, "#000000", new RectPt(10, 60, 80, 30));
            await d.SaveAsync(path, null, TestContext.Current.CancellationToken);
        }
        var dialogs = new FakeDialogs { OcrAnswer = new OcrRequest([0], SkipPagesWithText: false) };
        var vm = new MainViewModel(new AppSettings(), dialogs);
        await vm.OpenPathAsync(path);
        await vm.RecognizeTextCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.Errors);
        Assert.Contains(vm.Document!.Pages[0].OcrWords, w => w.Text == "Grüße");
        await vm.Document!.DisposeAsync();
    }
}

public class OcrWindowTests
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

    [AvaloniaFact]
    public async Task TheDialogReturnsTheChosenPagesOnOkAndNothingOnClose()
    {
        var owner = new Window { Width = 600, Height = 500 };
        owner.Show();

        var dialog = new OcrDialog(new OcrDialogContext(4, 1, []));
        var result = dialog.ShowDialog<OcrRequest?>(owner);
        await Pump();
        var skip = dialog.FindControl<CheckBox>("SkipBox")!;
        Assert.True(skip.IsChecked);
        dialog.MouseDown(CenterOf(skip, dialog), MouseButton.Left);  // untick "skip pages with text"
        dialog.MouseUp(CenterOf(skip, dialog), MouseButton.Left);
        Assert.False(skip.IsChecked);
        var ok = dialog.FindControl<Button>("OkButton")!;
        dialog.MouseDown(CenterOf(ok, dialog), MouseButton.Left);
        dialog.MouseUp(CenterOf(ok, dialog), MouseButton.Left);
        var request = await result;
        Assert.Equal([0, 1, 2, 3], request!.Pages);
        Assert.False(request.SkipPagesWithText);

        var closed = new OcrDialog(new OcrDialogContext(4, 1, []));
        var none = closed.ShowDialog<OcrRequest?>(owner);
        await Pump();
        closed.Close(); // [X] = cancel
        Assert.Null(await none);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task RibbonAndMenuOfferTheCommandOnceADocumentIsOpen()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        var button = window.FindControl<RibbonButton>("OcrButton")!;
        Assert.Same(window.ViewModel.RecognizeTextCommand, button.Command);
        Assert.False(window.ViewModel.RecognizeTextCommand.CanExecute(null));

        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        Assert.True(window.ViewModel.RecognizeTextCommand.CanExecute(null));
        Assert.True(button.IsEffectivelyEnabled);

        var edit = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Edit").Menu!;
        Assert.Contains(edit.Items.OfType<NativeMenuItem>(), i => i.Header == "Recognize Text (OCR)…" && i.Command == window.ViewModel.RecognizeTextCommand);
        window.Close();
        await window.ViewModel.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task CtrlCInOrganizeModeCopiesTheTextOfTheSelectedPages()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        var vm = window.ViewModel;
        var document = vm.Document!;
        document.SetOcrWords(new Dictionary<int, IReadOnlyList<OcrWord>>
        {
            [document.Pages[1].Id] = [new OcrWord("Seite", new RectPt(10, 10, 30, 12), 1), new OcrWord("zwei", new RectPt(45, 10, 25, 12), 1)],
        });

        var button = window.FindControl<RibbonButton>("CopyTextButton")!;
        Assert.Same(vm.CopyTextCommand, button.Command);
        var edit = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Edit").Menu!;
        Assert.Contains(edit.Items.OfType<NativeMenuItem>(), i => i.Header == "Copy Text" && i.Command == vm.CopyTextCommand);

        vm.IsOrganizeMode = true;
        vm.SelectAllCommand.Execute(null);
        await Pump();
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await Pump();
        Assert.Equal("Text of 1 page(s) copied to the clipboard (10 characters).", vm.StatusMessage);
        Assert.Equal("Seite zwei", await window.Clipboard!.TryGetTextAsync());

        // In a text box Ctrl+C keeps copying the box's text
        window.GetVisualDescendants().OfType<Ribbon>().Single().SelectedIndex = 2;
        await Pump();
        var zoomBox = window.FindControl<TextBox>("ZoomBox")!;
        zoomBox.Focus();
        zoomBox.SelectAll();
        vm.StatusMessage = "";
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await Pump();
        Assert.Equal("", vm.StatusMessage);
        Assert.Equal(zoomBox.Text, await window.Clipboard!.TryGetTextAsync());
        // Delete in a text box acts on the box only, not on the selected pages
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        await Pump();
        Assert.Equal(2, document.PageCount);
        Assert.Equal("", zoomBox.Text);
        // Outside a text box the shortcuts work as before
        window.FindControl<EasyPDF.App.Controls.ThumbnailGrid>("OrganizeView")!.Focus();
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await Pump();
        Assert.Equal("Seite zwei", await window.Clipboard!.TryGetTextAsync());
        window.Close();
        await document.DisposeAsync();
    }
}
