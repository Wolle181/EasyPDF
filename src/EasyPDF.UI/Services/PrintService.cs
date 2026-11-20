using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using EasyPDF.App.Dialogs;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.App.Services;

/// <param name="Printer">Name of the printer as listed by <see cref="IPrintService.GetPrinters"/>.</param>
/// <param name="Copies">Number of copies (at least 1).</param>
/// <param name="Pages">Zero-based pages of the document to print.</param>
/// <param name="Setup">Driver settings chosen with "Properties…" for this printer; null = the printer's defaults.</param>
public sealed record PrintRequest(string Printer, int Copies, IReadOnlyList<int> Pages, PrinterSetup? Setup = null);

/// <summary>What the print dialog needs to know.</summary>
/// <param name="Printing">Shows the printer properties; without it the dialog has no "Properties…" button.</param>
/// <param name="Setups">Driver settings chosen earlier in this session, per printer.</param>
/// <param name="SelectedPages">The selected pages (Organize mode): one is preselected as the current page, several as the page range; none means all pages.</param>
public sealed record PrintDialogContext(int PageCount, int CurrentPage, IReadOnlyList<string> Printers, string? DefaultPrinter,
    IPrintService? Printing = null, IReadOnlyDictionary<string, PrinterSetup>? Setups = null, IReadOnlyList<int>? SelectedPages = null);

/// <summary>
/// The printer driver's own settings (tray, duplex, paper, colour …) for one printer.
/// Windows: the DEVMODE the driver dialog returned; CUPS: the <c>-o name=value</c> options that differ from the printer's defaults.
/// </summary>
public sealed record PrinterSetup(string Printer, byte[]? DevMode, IReadOnlyDictionary<string, string> Options)
{
    public static PrinterSetup FromOptions(string printer, IReadOnlyDictionary<string, string> options) => new(printer, null, options);
}

/// <summary>A CUPS printer option (from the PPD) with its choices; <see cref="Default"/> is the current default choice.</summary>
public sealed record PrinterOption(string Key, string Label, IReadOnlyList<string> Choices, string Default);

/// <summary>Sends a finished PDF file (just the pages to print) to a printer.</summary>
public interface IPrintService
{
    IReadOnlyList<string> GetPrinters();

    string? GetDefaultPrinter();

    Task PrintAsync(string pdfPath, string printer, int copies, PrinterSetup? setup, IProgress<double>? progress, CancellationToken token);

    /// <summary>Shows the printer's own settings (Windows: the driver's dialog; CUPS: its options); null if cancelled.</summary>
    Task<PrinterSetup?> EditSetupAsync(Window owner, string printer, PrinterSetup? current);
}

/// <summary>
/// Windows: draws each page, rendered by pdfium, through the .NET print system; "Properties…" is the driver's own dialog.
/// macOS and Linux: hands the file to CUPS (<c>lp</c>); "Properties…" lists the printer's CUPS options in our own dialog.
/// </summary>
public sealed class PlatformPrintService : IPrintService
{
    public static PlatformPrintService Instance { get; } = new();

    public IReadOnlyList<string> GetPrinters() => OperatingSystem.IsWindows() ? WindowsPrinting.GetPrinters() : Cups.GetPrinters();

    public string? GetDefaultPrinter() => OperatingSystem.IsWindows() ? WindowsPrinting.GetDefaultPrinter() : Cups.GetDefaultPrinter();

    public Task PrintAsync(string pdfPath, string printer, int copies, PrinterSetup? setup, IProgress<double>? progress, CancellationToken token)
    {
        setup = setup?.Printer == printer ? setup : null;
        return OperatingSystem.IsWindows()
            ? WindowsPrinting.PrintAsync(pdfPath, printer, copies, setup?.DevMode, progress, token)
            : Cups.PrintAsync(pdfPath, printer, copies, setup?.Options, token);
    }

    public async Task<PrinterSetup?> EditSetupAsync(Window owner, string printer, PrinterSetup? current)
    {
        current = current?.Printer == printer ? current : null;
        if (OperatingSystem.IsWindows())
            return WindowsPrinting.EditSetup(owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, printer, current?.DevMode);
        var options = await Task.Run(() => Cups.GetOptions(printer));
        var chosen = await new PrinterOptionsDialog(printer, options, current?.Options).ShowDialog<IReadOnlyDictionary<string, string>?>(owner);
        return chosen == null ? null : PrinterSetup.FromOptions(printer, chosen);
    }
}

public static class Cups
{
    private static string? Run(string program, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args)
                info.ArgumentList.Add(a);
            using var process = Process.Start(info);
            if (process == null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null; // CUPS is not installed
        }
    }

    public static IReadOnlyList<string> GetPrinters() =>
        Run("lpstat", "-e")?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    public static string? GetDefaultPrinter()
    {
        // "system default destination: Name"
        var line = Run("lpstat", "-d")?.Trim();
        var colon = line?.IndexOf(':') ?? -1;
        return colon > 0 ? line![(colon + 1)..].Trim() : null;
    }

    /// <summary>The PPD options of the printer (<c>lpoptions -l</c>); empty if CUPS knows none.</summary>
    public static IReadOnlyList<PrinterOption> GetOptions(string printer) => ParseOptions(Run("lpoptions", "-p", printer, "-l") ?? "");

    /// <summary>Parses lines like <c>InputSlot/Media Source: *Auto Tray1 Tray2</c> (the star marks the default).</summary>
    public static IReadOnlyList<PrinterOption> ParseOptions(string output)
    {
        var options = new List<PrinterOption>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.LastIndexOf(": ", StringComparison.Ordinal); // choices are single words, the label may contain a colon
            if (colon <= 0)
                continue;
            var name = line[..colon];
            var slash = name.IndexOf('/');
            var key = slash > 0 ? name[..slash] : name;
            var label = slash > 0 && slash < name.Length - 1 ? name[(slash + 1)..] : key;
            var choices = new List<string>();
            string? current = null;
            foreach (var token in line[(colon + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var choice = token.TrimStart('*');
                if (choice.Length == 0 || choice.StartsWith("Custom.", StringComparison.Ordinal))
                    continue; // custom sizes need extra values
                choices.Add(choice);
                if (token.StartsWith('*'))
                    current = choice;
            }
            if (choices.Count > 0)
                options.Add(new PrinterOption(key, label, choices, current ?? choices[0]));
        }
        return options;
    }

    public static async Task PrintAsync(string pdfPath, string printer, int copies, IReadOnlyDictionary<string, string>? options, CancellationToken token)
    {
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo("lp") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-d", printer, "-n", Math.Max(1, copies).ToString(), "-o", "fit-to-page" })
                info.ArgumentList.Add(a);
            foreach (var (key, value) in options ?? new Dictionary<string, string>())
            {
                info.ArgumentList.Add("-o");
                info.ArgumentList.Add(key + "=" + value);
            }
            info.ArgumentList.Add(pdfPath);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("lp could not be started.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error.Trim().Length > 0 ? error.Trim() : $"lp failed with exit code {process.ExitCode}.");
        }, token).ConfigureAwait(false);
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsPrinting
{
    private const int Dpi = 300;
    private const int DmOutBuffer = 2, DmInPrompt = 4, DmInBuffer = 8, IdOk = 1;
    private const uint GmemMoveable = 2;

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv")]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode)]
    private static extern int DocumentProperties(IntPtr owner, IntPtr printer, string deviceName, IntPtr output, IntPtr input, int mode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);

    public static IReadOnlyList<string> GetPrinters()
    {
        var list = new List<string>();
        foreach (string name in PrinterSettings.InstalledPrinters)
            list.Add(name);
        return list;
    }

    public static string? GetDefaultPrinter()
    {
        var settings = new PrinterSettings();
        return settings.IsValid ? settings.PrinterName : null;
    }

    /// <summary>The printer's default DEVMODE, where the driver dialog starts the first time.</summary>
    public static byte[] DefaultDevMode(string printer) =>
        WithDriver(printer, (handle, size) => Call(handle, printer, size, null, IntPtr.Zero, DmOutBuffer) ?? throw NoSettings(printer));

    /// <summary>
    /// Shows the driver's own settings dialog (tray, duplex, paper …), modal to <paramref name="owner"/>, starting from
    /// <paramref name="current"/> or the printer's defaults; returns the new DEVMODE, or null if cancelled.
    /// </summary>
    public static PrinterSetup? EditSetup(IntPtr owner, string printer, byte[]? current) => WithDriver(printer, (handle, size) =>
    {
        var start = current?.Length == size ? current : Call(handle, printer, size, null, IntPtr.Zero, DmOutBuffer) ?? throw NoSettings(printer);
        var devMode = Call(handle, printer, size, start, owner, DmInBuffer | DmInPrompt | DmOutBuffer);
        return devMode == null ? null : new PrinterSetup(printer, devMode, new Dictionary<string, string>()); // null: cancelled
    });

    private static InvalidOperationException NoSettings(string printer) => new($"The driver of “{printer}” did not return its settings.");

    /// <summary>Opens the printer and asks its driver for the size of its DEVMODE (including the driver's private part).</summary>
    private static T WithDriver<T>(string printer, Func<IntPtr, int, T> use)
    {
        if (!OpenPrinter(printer, out var handle, IntPtr.Zero))
            throw new InvalidOperationException($"The printer “{printer}” is not available.");
        try
        {
            var size = DocumentProperties(IntPtr.Zero, handle, printer, IntPtr.Zero, IntPtr.Zero, 0);
            return size > 0 ? use(handle, size) : throw NoSettings(printer);
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>One <c>DocumentProperties</c> call with <paramref name="input"/> as the starting DEVMODE; null unless the driver answers IDOK.</summary>
    private static byte[]? Call(IntPtr handle, string printer, int size, byte[]? input, IntPtr owner, int mode)
    {
        var inBuffer = Marshal.AllocHGlobal(size);
        var outBuffer = Marshal.AllocHGlobal(size);
        try
        {
            if (input != null)
                Marshal.Copy(input, 0, inBuffer, size);
            if (DocumentProperties(owner, handle, printer, outBuffer, input != null ? inBuffer : IntPtr.Zero, mode) != IdOk)
                return null;
            var output = new byte[size];
            Marshal.Copy(outBuffer, output, 0, size);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    /// <param name="printToFile">Lets the driver write into this file instead of printing (for tests with "Microsoft Print to PDF").</param>
    public static async Task PrintAsync(string pdfPath, string printer, int copies, byte[]? devMode, IProgress<double>? progress, CancellationToken token,
        string? printToFile = null)
    {
        await using var source = await PdfSource.OpenAsync(pdfPath, null, token);
        await source.LoadPageSizesAsync();
        // The print system calls back synchronously, so it runs on its own thread; pdfium work still goes through its worker
        await Task.Run(() => Print(source, printer, copies, devMode, progress, printToFile, token), token).ConfigureAwait(false);
    }

    /// <summary>Hands the driver settings to the print job; printer and page settings both take theirs (tray, paper, duplex …) from it.</summary>
    public static void ApplyDevMode(PrintDocument document, byte[] devMode)
    {
        var handle = GlobalAlloc(GmemMoveable, (nuint)devMode.Length);
        if (handle == IntPtr.Zero)
            throw new OutOfMemoryException();
        try
        {
            Marshal.Copy(devMode, 0, GlobalLock(handle), devMode.Length);
            GlobalUnlock(handle);
            document.PrinterSettings.SetHdevmode(handle);
            document.DefaultPageSettings.SetHdevmode(handle);
        }
        finally
        {
            GlobalFree(handle);
        }
    }

    private static void Print(PdfSource source, string printer, int copies, byte[]? devMode, IProgress<double>? progress, string? printToFile,
        CancellationToken token)
    {
        using var document = new PrintDocument();
        document.PrinterSettings.PrinterName = printer;
        if (printToFile != null)
        {
            document.PrinterSettings.PrintToFile = true;
            document.PrinterSettings.PrintFileName = printToFile;
        }
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"The printer “{printer}” is not available.");
        if (devMode != null)
            ApplyDevMode(document, devMode);
        document.PrinterSettings.Copies = (short)Math.Clamp(copies, 1, short.MaxValue);
        document.DocumentName = Localization.Loc.T("AppName");
        var next = 0;
        document.QueryPageSettings += (_, e) =>
        {
            // Portrait or landscape follows each page, so it always fits; everything else comes from the driver settings
            var size = source.GetPageSizeOrEstimate(Math.Min(next, source.PageCount - 1));
            e.PageSettings.Landscape = size.Width > size.Height;
        };
        document.PrintPage += (_, e) =>
        {
            if (token.IsCancellationRequested || e.Graphics == null)
            {
                e.Cancel = true;
                return;
            }
            DrawPage(source, next, e.Graphics, token);
            next++;
            progress?.Report((double)next / source.PageCount);
            e.HasMorePages = next < source.PageCount;
        };
        document.Print();
    }

    /// <summary>Fits the page into the printable area (units are 1/100 inch) and draws it rendered at 300 dpi.</summary>
    private static void DrawPage(PdfSource source, int index, Graphics graphics, CancellationToken token)
    {
        var area = graphics.VisibleClipBounds;
        var size = source.GetPageSizeOrEstimate(index);
        var (w, h) = (size.Width * 100 / 72, size.Height * 100 / 72); // page size in 1/100 inch
        var scale = Math.Min(area.Width / w, area.Height / h);
        var (drawW, drawH) = ((float)(w * scale), (float)(h * scale));
        var pixelW = Math.Max(1, (int)Math.Round(drawW / 100 * Dpi));
        var pixelH = Math.Max(1, (int)Math.Round(drawH / 100 * Dpi));
        var page = source.RenderAsync(index, pixelW, pixelH, 0, PdfiumWorker.PriorityBackground, token).GetAwaiter().GetResult();
        using var bitmap = new Bitmap(page.Width, page.Height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, page.Width, page.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var y = 0; y < page.Height; y++)
                Marshal.Copy(page.Pixels, y * page.Stride, data.Scan0 + y * data.Stride, page.Width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        graphics.DrawImage(bitmap, new RectangleF(area.X + (area.Width - drawW) / 2, area.Y + (area.Height - drawH) / 2, drawW, drawH));
    }
}
