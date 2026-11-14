using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class PrintTests
{
    private sealed class FakePrinting : IPrintService
    {
        public readonly List<(string Printer, int Copies, int Pages, bool Existed)> Jobs = [];
        public readonly List<PrinterSetup?> Setups = [];
        public readonly List<(string Printer, PrinterSetup? Current)> Edits = [];
        public Func<string, PrinterSetup?>? EditAnswer;
        public IReadOnlyList<string> GetPrinters() => ["Office", "PDF"];
        public string? GetDefaultPrinter() => "PDF";

        public async Task PrintAsync(string pdfPath, string printer, int copies, PrinterSetup? setup, IProgress<double>? progress, CancellationToken token)
        {
            await using var source = await PdfSource.OpenAsync(pdfPath);
            Jobs.Add((printer, copies, source.PageCount, true));
            Setups.Add(setup);
        }

        public Task<PrinterSetup?> EditSetupAsync(Window owner, string printer, PrinterSetup? current)
        {
            Edits.Add((printer, current));
            return Task.FromResult(EditAnswer?.Invoke(printer));
        }
    }

    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static async Task Click(Window window, Control target)
    {
        var p = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();
    }

    private static Button ButtonOf(Window window, string key) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == Loc.T(key));

    private static PrinterSetup Tray2(string printer) => PrinterSetup.FromOptions(printer, new Dictionary<string, string> { ["InputSlot"] = "Tray2" });

    [Fact]
    public async Task PrintsOnlyTheChosenPagesAndCleansUp()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var printing = new FakePrinting();
        var vm = new MainViewModel(new AppSettings(), dialogs, printing);
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 6));
        vm.CurrentPage = 2;

        // Cancelled dialog: nothing printed
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Empty(printing.Jobs);
        Assert.Equal(6, dialogs.LastPrintContext!.PageCount);
        Assert.Equal(2, dialogs.LastPrintContext.CurrentPage);
        Assert.Equal("PDF", dialogs.LastPrintContext.DefaultPrinter);

        dialogs.PrintAnswer = new PrintRequest("Office", 2, [2]);          // current page
        await vm.PrintCommand.ExecuteAsync(null);
        dialogs.PrintAnswer = new PrintRequest("Office", 1, [1, 2, 3]);    // range 2-4
        await vm.PrintCommand.ExecuteAsync(null);
        dialogs.PrintAnswer = new PrintRequest("PDF", 1, [0, 1, 2, 3, 4, 5]); // all
        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Equal([("Office", 2, 1, true), ("Office", 1, 3, true), ("PDF", 1, 6, true)], printing.Jobs);
        Assert.Empty(dialogs.Errors);
        Assert.False(vm.IsBusy);
        Assert.False(vm.Document!.IsDirty); // printing does not change the document
        await vm.Document.DisposeAsync();
    }

    [Fact]
    public void ListingThePrintersOfThisMachineDoesNotThrow()
    {
        var printers = PlatformPrintService.Instance.GetPrinters();
        var def = PlatformPrintService.Instance.GetDefaultPrinter();
        Assert.True(def == null || printers.Count >= 0);
    }

    [Fact]
    public async Task PrintIsEnabledOnlyWithADocumentAndTheContextPageIsTheDefault()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new AppSettings(), dialogs, new FakePrinting());
        Assert.False(vm.PrintCommand.CanExecute(null));
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 5));
        Assert.True(vm.PrintCommand.CanExecute(null));

        vm.CurrentPage = 1;
        await vm.PrintCommand.ExecuteAsync(4); // right click on page 5
        Assert.Equal(4, dialogs.LastPrintContext!.CurrentPage);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task TheChosenDriverSettingsGoToThePrinterAndAreOfferedAgainNextTime()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var printing = new FakePrinting();
        var vm = new MainViewModel(new AppSettings(), dialogs, printing);
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 2));

        dialogs.PrintAnswer = new PrintRequest("Office", 1, [0], Tray2("Office"));
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Same(printing, dialogs.LastPrintContext!.Printing);
        Assert.Equal("Tray2", printing.Setups.Single()!.Options["InputSlot"]);

        dialogs.PrintAnswer = null;
        await vm.PrintCommand.ExecuteAsync(null);
        Assert.Equal("Tray2", dialogs.LastPrintContext!.Setups!["Office"].Options["InputSlot"]); // kept for the next print job
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public void CupsOptionsAreReadWithTheirDefaultsAndWithoutCustomSizes()
    {
        var options = Cups.ParseOptions("""
            PageSize/Media Size: Letter *A4 Legal Custom.WIDTHxHEIGHT
            InputSlot/Media Source: *Auto Tray1 Tray2 Manual
            Duplex/2-Sided Printing: *None DuplexNoTumble DuplexTumble
            Resolution/Output Resolution: 300dpi *600dpi
            garbage line
            """);
        Assert.Equal(["PageSize", "InputSlot", "Duplex", "Resolution"], options.Select(o => o.Key));
        Assert.Equal("Media Source", options[1].Label);
        Assert.Equal(["Letter", "A4", "Legal"], options[0].Choices);
        Assert.Equal("A4", options[0].Default);
        Assert.Equal("600dpi", options[3].Default);
        Assert.Empty(Cups.ParseOptions(""));
    }

    [AvaloniaFact]
    public async Task ThePrinterOptionsDialogReturnsOnlyTheChangedOptions()
    {
        var owner = new Window { Width = 600, Height = 500 };
        owner.Show();
        PrinterOption[] options =
        [
            new("InputSlot", "Media Source", ["Auto", "Tray1", "Tray2"], "Auto"),
            new("Duplex", "2-Sided Printing", ["None", "DuplexNoTumble"], "None"),
        ];

        // Starts from the earlier choice; only differences from the defaults come back
        var dialog = new PrinterOptionsDialog("Office", options, new Dictionary<string, string> { ["InputSlot"] = "Tray1" });
        var result = dialog.ShowDialog<IReadOnlyDictionary<string, string>?>(owner);
        await Pump();
        var boxes = dialog.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Equal("Tray1", boxes[0].SelectedItem);
        Assert.Equal("None", boxes[1].SelectedItem);
        boxes[1].SelectedItem = "DuplexNoTumble";
        await Click(dialog, ButtonOf(dialog, "Ok"));
        Assert.Equal(new Dictionary<string, string> { ["InputSlot"] = "Tray1", ["Duplex"] = "DuplexNoTumble" }, await result);

        // "Printer defaults" resets every choice
        var reset = new PrinterOptionsDialog("Office", options, new Dictionary<string, string> { ["InputSlot"] = "Tray2" });
        var empty = reset.ShowDialog<IReadOnlyDictionary<string, string>?>(owner);
        await Pump();
        await Click(reset, ButtonOf(reset, "PrinterOptionsReset"));
        await Click(reset, ButtonOf(reset, "Ok"));
        Assert.Empty((await empty)!);

        // [X] = cancel; a printer without options says so
        var closed = new PrinterOptionsDialog("PDF", [], null);
        var none = closed.ShowDialog<IReadOnlyDictionary<string, string>?>(owner);
        await Pump();
        Assert.Contains(closed.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("PrinterOptionsNone"));
        closed.Close();
        Assert.Null(await none);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task PropertiesOpensTheSettingsOfTheSelectedPrinterAndTheyArePrintedWith()
    {
        var owner = new Window { Width = 600, Height = 500 };
        owner.Show();
        var printing = new FakePrinting { EditAnswer = Tray2 };
        var dialog = new PrintDialog(new PrintDialogContext(3, 0, printing.GetPrinters(), "PDF", printing,
            new Dictionary<string, PrinterSetup> { ["PDF"] = Tray2("PDF") }));
        var result = dialog.ShowDialog<PrintRequest?>(owner);
        await Pump();
        var hint = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Loc.T("PrintCustomSetup"));
        Assert.True(hint.IsVisible); // settings from the last print job

        var printer = dialog.GetVisualDescendants().OfType<ComboBox>().Single();
        printer.SelectedItem = "Office";
        await Pump();
        Assert.False(hint.IsVisible);
        await Click(dialog, ButtonOf(dialog, "PrintProperties"));
        Assert.Equal(("Office", (PrinterSetup?)null), printing.Edits.Single());
        Assert.True(hint.IsVisible);

        printing.EditAnswer = _ => null; // cancelling the properties keeps the settings
        await Click(dialog, ButtonOf(dialog, "PrintProperties"));
        Assert.Equal("Tray2", printing.Edits[1].Current!.Options["InputSlot"]);
        await Click(dialog, ButtonOf(dialog, "PrintOk"));
        var request = await result;
        Assert.Equal("Office", request!.Printer);
        Assert.Equal("Tray2", request.Setup!.Options["InputSlot"]);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task WithoutAPrintServiceTheDialogHasNoPropertiesButton()
    {
        var owner = new Window { Width = 600, Height = 500 };
        owner.Show();
        var dialog = new PrintDialog(new PrintDialogContext(1, 0, ["PDF"], "PDF"));
        var result = dialog.ShowDialog<PrintRequest?>(owner);
        await Pump();
        Assert.False(ButtonOf(dialog, "PrintProperties").IsVisible);
        dialog.Close();
        Assert.Null(await result);
        owner.Close();
    }

    [Fact]
    public void WindowsDriverSettingsReachThePrintJob()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            return;
        var printer = WindowsPrinting.GetDefaultPrinter();
        if (printer == null)
            return; // no printer installed (CI)

        // A changed DEVMODE (paper A5, as the driver dialog would return it) ends up in the printer and page settings
        var devMode = WindowsPrinting.DefaultDevMode(printer);
        Assert.True(devMode.Length >= 220); // public DEVMODEW part, plus the driver's private data
        const int fields = 72, paperSize = 78, dmPaperSize = 0x2, a5 = 11;
        BitConverter.GetBytes(BitConverter.ToUInt32(devMode, fields) | dmPaperSize).CopyTo(devMode, fields);
        BitConverter.GetBytes((short)a5).CopyTo(devMode, paperSize);
        using var document = new System.Drawing.Printing.PrintDocument();
        document.PrinterSettings.PrinterName = printer;
        WindowsPrinting.ApplyDevMode(document, devMode);
        Assert.Equal(a5, document.DefaultPageSettings.PaperSize.RawKind);
        Assert.Equal(printer, document.PrinterSettings.PrinterName);
    }

    [Fact]
    public async Task WindowsPrintsEveryPageOfTheFile()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1) || !WindowsPrinting.GetPrinters().Contains("Microsoft Print to PDF"))
            return; // the printer that writes into a file is missing (CI)
        using var dir = new TempDir();
        var output = dir.File("printed.pdf");
        await WindowsPrinting.PrintAsync(dir.Pdf("a.pdf", 3), "Microsoft Print to PDF", 1, null, null, TestContext.Current.CancellationToken, output);
        await using var printed = await PdfSource.OpenAsync(output, null, TestContext.Current.CancellationToken);
        Assert.Equal(3, printed.PageCount);
    }

    [AvaloniaFact]
    public async Task TheDialogPrintsAllPagesByDefault()
    {
        var owner = new Window { Width = 600, Height = 500 };
        owner.Show();
        var dialog = new PrintDialog(new PrintDialogContext(5, 2, ["PDF"], "PDF"));
        var result = dialog.ShowDialog<PrintRequest?>(owner);
        await Pump();
        var radios = dialog.GetVisualDescendants().OfType<RadioButton>().ToList(); // current page, range, all pages
        Assert.Equal([false, false, true], radios.Select(r => r.IsChecked == true));
        await Click(dialog, ButtonOf(dialog, "PrintOk"));
        Assert.Equal([0, 1, 2, 3, 4], (await result)!.Pages);

        // The current page can still be chosen
        var current = new PrintDialog(new PrintDialogContext(5, 2, ["PDF"], "PDF"));
        var single = current.ShowDialog<PrintRequest?>(owner);
        await Pump();
        await Click(current, current.GetVisualDescendants().OfType<RadioButton>().First());
        await Click(current, ButtonOf(current, "PrintOk"));
        Assert.Equal([2], (await single)!.Pages);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task SeveralSelectedPagesArePreselectedAsThePageRangeOneAsTheCurrentPage()
    {
        var owner = new Window { Width = 600, Height = 500 };
        owner.Show();

        // Gaps in the selection are kept: exactly the selected pages are printed
        var several = new PrintDialog(new PrintDialogContext(9, 0, ["PDF"], "PDF", SelectedPages: [1, 2, 3, 6]));
        var severalResult = several.ShowDialog<PrintRequest?>(owner);
        await Pump();
        Assert.Equal([false, true, false], several.GetVisualDescendants().OfType<RadioButton>().Select(r => r.IsChecked == true));
        Assert.Contains(several.GetVisualDescendants().OfType<TextBox>(), b => b.Text == "2-4, 7");
        await Click(several, ButtonOf(several, "PrintOk"));
        Assert.Equal([1, 2, 3, 6], (await severalResult)!.Pages);

        var one = new PrintDialog(new PrintDialogContext(9, 4, ["PDF"], "PDF", SelectedPages: [4]));
        var oneResult = one.ShowDialog<PrintRequest?>(owner);
        await Pump();
        Assert.Equal([true, false, false], one.GetVisualDescendants().OfType<RadioButton>().Select(r => r.IsChecked == true));
        await Click(one, ButtonOf(one, "PrintOk"));
        Assert.Equal([4], (await oneResult)!.Pages);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TypingARangeChoosesItAndAWrongOneIsExplained()
    {
        var owner = new Window { Width = 600, Height = 500 };
        owner.Show();
        var dialog = new PrintDialog(new PrintDialogContext(5, 1, ["PDF"], "PDF"));
        var result = dialog.ShowDialog<PrintRequest?>(owner);
        await Pump();
        var box = dialog.GetVisualDescendants().OfType<TextBox>().Single(b => b.Text == "2-5");

        box.Text = "9";
        await Pump();
        Assert.Equal([false, true, false], dialog.GetVisualDescendants().OfType<RadioButton>().Select(r => r.IsChecked == true));
        Assert.False(ButtonOf(dialog, "PrintOk").IsEnabled);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.F("ErrRangeOutOfRange", "9", Loc.Pages(5)));

        box.Text = "1, 3";
        await Pump();
        await Click(dialog, ButtonOf(dialog, "PrintOk"));
        Assert.Equal([0, 2], (await result)!.Pages);
        owner.Close();
    }

    [Fact]
    public async Task TheSelectionOfTheOrganizeModeReachesTheDialogsAndOnePageIsTheCurrentPage()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new AppSettings(), dialogs, new FakePrinting());
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 6));
        vm.CurrentPage = 0;
        vm.IsOrganizeMode = true;

        // One selected page: it is the current page of each dialog, also when CurrentPage is another one
        vm.SetSelection([vm.Document!.Pages[3].Id]);
        await vm.PrintCommand.ExecuteAsync(null);
        await vm.RotatePagesCommand.ExecuteAsync(null);
        await vm.ReplacePagesCommand.ExecuteAsync(null);
        await vm.RecognizeTextCommand.ExecuteAsync(null);
        Assert.Equal(3, dialogs.LastPrintContext!.CurrentPage);
        var rotate = new RotateDialogViewModel(dialogs.LastRotateContext!);
        Assert.Equal(RotateScope.Current, rotate.Scope);
        Assert.Equal([3], rotate.BuildResult()!.Pages);
        await using (var replace = new ReplacePagesDialogViewModel(dialogs.LastReplaceContext!))
        {
            Assert.Equal(ReplaceTarget.Current, replace.Target);
            Assert.Contains("4", replace.CurrentPageText);
        }
        var ocr = new OcrDialogViewModel(dialogs.LastOcrContext!);
        Assert.Equal(RotateScope.Current, ocr.Scope);
        Assert.Equal([3], ocr.BuildRequest()!.Pages);

        // Several selected pages
        vm.SetSelection([vm.Document.Pages[1].Id, vm.Document.Pages[4].Id]);
        await vm.PrintCommand.ExecuteAsync(4);
        await vm.RotatePagesCommand.ExecuteAsync(4);
        await vm.ReplacePagesCommand.ExecuteAsync(4);
        await vm.RecognizeTextCommand.ExecuteAsync(4);
        Assert.Equal([1, 4], dialogs.LastPrintContext.SelectedPages);
        Assert.Equal(RotateScope.Selected, new RotateDialogViewModel(dialogs.LastRotateContext!).Scope);
        await using (var replace = new ReplacePagesDialogViewModel(dialogs.LastReplaceContext!))
            Assert.Equal(ReplaceTarget.Selected, replace.Target);
        Assert.Equal(RotateScope.Selected, new OcrDialogViewModel(dialogs.LastOcrContext!).Scope);

        // No selection: the defaults of each dialog
        vm.SetSelection([]);
        await vm.RecognizeTextCommand.ExecuteAsync(null);
        await vm.RotatePagesCommand.ExecuteAsync(null);
        Assert.Equal(RotateScope.All, new OcrDialogViewModel(dialogs.LastOcrContext!).Scope);
        Assert.Equal(RotateScope.Current, new RotateDialogViewModel(dialogs.LastRotateContext!).Scope);
        await vm.Document.DisposeAsync();
    }
}
