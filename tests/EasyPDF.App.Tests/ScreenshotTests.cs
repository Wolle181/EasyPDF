using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using EasyPDF.App.Views;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>
/// Renders the main screens to PNG files for reviewing the layout by eye.
/// Only writes when EASYPDF_SCREENSHOTS names a folder; otherwise it just renders.
/// </summary>
public class ScreenshotTests
{
    private static string DemoPdf()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "EasyPDF.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "demo-pdfs", "Annual Report 2025.pdf");
    }

    private static async Task Pump(int ms = 600)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static void Save(Avalonia.Controls.Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var folder = Environment.GetEnvironmentVariable("EASYPDF_SCREENSHOTS");
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    /// <summary>The print dialog (with custom driver settings) and the CUPS printer options.</summary>
    private static async Task SavePrintDialogs(string prefix)
    {
        var setup = EasyPDF.App.Services.PrinterSetup.FromOptions("Office", new Dictionary<string, string> { ["InputSlot"] = "Tray2" });
        var print = new EasyPDF.App.Dialogs.PrintDialog(new EasyPDF.App.Services.PrintDialogContext(52, 4, ["Office", "Microsoft Print to PDF"], "Office",
            EasyPDF.App.Services.PlatformPrintService.Instance, new Dictionary<string, EasyPDF.App.Services.PrinterSetup> { ["Office"] = setup }));
        print.Show();
        await Pump();
        Save(print, $"{prefix}-dialog-print");
        print.Close();

        var options = new EasyPDF.App.Dialogs.PrinterOptionsDialog("Office",
            EasyPDF.App.Services.Cups.ParseOptions("""
                PageSize/Media Size: Letter *A4 A5 Legal
                InputSlot/Media Source: *Auto Tray1 Tray2 Manual
                Duplex/2-Sided Printing: *None DuplexNoTumble DuplexTumble
                ColorModel/Output Mode: *RGB Gray
                """), setup.Options);
        options.Show();
        await Pump();
        Save(options, $"{prefix}-dialog-printer-options");
        options.Close();
    }

    /// <summary>German texts are longer: the ribbon and the merge dialog in German.</summary>
    [AvaloniaFact]
    public async Task GermanScreens()
    {
        var old = System.Globalization.CultureInfo.CurrentUICulture;
        EasyPDF.App.Localization.Loc.SetCulture(new System.Globalization.CultureInfo("de"));
        try
        {
            var window = new MainWindow { Width = 1200, Height = 760 };
            window.Show();
            await window.OpenPathAsync(DemoPdf());
            await Pump(1500);
            Save(window, "de-reading");
            window.FindControl<EasyPDF.App.Controls.Ribbon.Ribbon>("MainRibbon")!.SelectedIndex = 1;
            await Pump();
            Save(window, "de-tab-organize");
            window.Close();

            using var dir = new TempDir();
            var concat = new EasyPDF.App.Dialogs.ConcatenateDialog(
                new EasyPDF.App.Services.ConcatenateContext(false, [dir.Pdf("Bericht 2026.pdf", 3), dir.Pdf("Anhang.pdf", 2)]), 0);
            concat.Show();
            await Pump();
            Save(concat, "de-dialog-concatenate");
            concat.Close();
            await SavePrintDialogs("de");
        }
        finally
        {
            EasyPDF.App.Localization.Loc.SetCulture(old);
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task MainScreens(string theme)
    {
        Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            var window = new MainWindow { Width = 1200, Height = 760 };
            window.Show();
            await Pump();
            Save(window, $"{theme}-start");

            await window.OpenPathAsync(DemoPdf());
            await Pump(1500);
            Save(window, $"{theme}-reading");

            // Black out and white out: a box of each on the first page, the black-out tool on
            var vm = window.ViewModel;
            var size = vm.Document!.Pages[0].DisplaySize;
            vm.PlaceRedactions([(0, new EasyPDF.Core.Documents.RectPt(size.Width * 0.1, size.Height * 0.3, size.Width * 0.5, size.Height * 0.04))], EasyPDF.Core.Documents.RedactionFill.Black);
            vm.PlaceRedactions([(0, new EasyPDF.Core.Documents.RectPt(size.Width * 0.1, size.Height * 0.4, size.Width * 0.4, size.Height * 0.08))], EasyPDF.Core.Documents.RedactionFill.White);
            vm.RedactCommand.Execute(null);
            await Pump();
            Save(window, $"{theme}-redact");
            vm.RedactCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            await Pump();

            window.ViewModel.ShowOrganizeCommand.Execute(null);
            await Pump(1500);
            Save(window, $"{theme}-organize");

            // The other ribbon tabs (switched-on buttons: current mode, page sidebar)
            var ribbon = window.FindControl<EasyPDF.App.Controls.Ribbon.Ribbon>("MainRibbon")!;
            ribbon.SelectedIndex = 1;
            await Pump();
            Save(window, $"{theme}-tab-organize");
            window.ViewModel.ShowReadingCommand.Execute(null);
            ribbon.SelectedIndex = 2;
            await Pump(800);
            Save(window, $"{theme}-tab-view");
            window.Close();

            using var dir = new TempDir();
            var concat = new EasyPDF.App.Dialogs.ConcatenateDialog(
                new EasyPDF.App.Services.ConcatenateContext(false, [dir.Pdf("Report 2026.pdf", 3), dir.Pdf("Appendix.pdf", 2), dir.Pdf("Cover letter.pdf", 1)]), 0);
            concat.Show();
            await Pump();
            Save(concat, $"{theme}-dialog-concatenate");
            concat.Close();

            var settings = new EasyPDF.App.Dialogs.SettingsWindow(new EasyPDF.Core.Settings.AppSettings(), () => { });
            settings.Show();
            await Pump();
            Save(settings, $"{theme}-dialog-settings");
            settings.Close();

            var guide = new EasyPDF.App.Dialogs.QuickGuideWindow();
            guide.Show();
            await Pump();
            Save(guide, $"{theme}-dialog-guide");
            guide.Close();

            var unsaved = new EasyPDF.App.Dialogs.UnsavedChangesDialog("Report 2026.pdf");
            unsaved.Show();
            await Pump();
            Save(unsaved, $"{theme}-dialog-unsaved");
            unsaved.Close();

            var redacted = new EasyPDF.App.Dialogs.RedactedSaveDialog("Report 2026.pdf", 3);
            redacted.Show();
            await Pump();
            Save(redacted, $"{theme}-dialog-redacted-save");
            redacted.Close();

            var about = new EasyPDF.App.Dialogs.AboutWindow();
            about.Show();
            await Pump();
            Save(about, $"{theme}-dialog-about");
            about.Close();
            await SavePrintDialogs(theme);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }
}
