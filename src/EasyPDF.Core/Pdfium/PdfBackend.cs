using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;

namespace EasyPDF.Core.Pdfium;

/// <summary>
/// Whatever reads or writes whole files: opening, saving, blank pages. The desktop program works on local files with
/// pdfium (<see cref="LocalPdfBackend"/>, the default); the web version sets a backend whose files live on the server.
/// </summary>
public interface IPdfBackend
{
    /// <summary>Whether <paramref name="path"/> names a file that can be opened.</summary>
    bool Exists(string path);

    /// <summary>
    /// Whether "Save" may write to <paramref name="path"/> without asking for a target (always for local files; the web
    /// version cannot write back to an uploaded file and asks for a target every time).
    /// </summary>
    bool CanOverwrite(string path);

    /// <summary>Size of the file at <paramref name="path"/> in bytes; null if unknown.</summary>
    long? FileSize(string path);

    Task<IPdfSource> OpenAsync(string path, CancellationToken token = default);

    /// <summary>A new source with one empty page of the given size (the caller owns it).</summary>
    Task<IPdfSource> CreateBlankAsync(PageSizePt size, CancellationToken token = default);

    /// <summary>
    /// Writes <paramref name="pages"/> based on <paramref name="baseSource"/> (keeps its bookmarks and metadata) to
    /// <paramref name="path"/> and opens the result. The sources in <paramref name="openSources"/> that are the target file
    /// are closed for the replace (and reopened if it fails). Returns the newly opened result (all page sizes read) and how
    /// many pictures were scaled down.
    /// </summary>
    Task<(IPdfSource Saved, int ReducedImages)> SaveAsync(IReadOnlyList<PageEntry> pages, IPdfSource baseSource, string path,
        IReadOnlyList<IPdfSource> openSources, IProgress<double>? progress = null, CancellationToken token = default,
        ImageReduction? reduceImages = null);

    /// <summary>Saves the given pages (current order + rotation) as a new PDF without bookmarks.</summary>
    Task ExtractAsync(IReadOnlyList<PageEntry> pages, string targetPath, IProgress<double>? progress = null, CancellationToken token = default);

    /// <summary>
    /// Writes each part as <c>&lt;baseName&gt;_partNN.pdf</c>: locally into the folder <paramref name="target"/>, in the web
    /// version as one ZIP file to the target the user chose. Returns the number of files.
    /// </summary>
    Task<int> SplitAsync(IReadOnlyList<PageEntry> pages, IReadOnlyList<SplitPart> parts, string target, string baseName,
        IProgress<double>? progress = null, CancellationToken token = default);
}

/// <summary>The backend the program uses (local pdfium unless the web version sets its own at start-up).</summary>
public static class PdfBackend
{
    public static IPdfBackend Current { get; set; } = new LocalPdfBackend();
}

/// <summary>Local files, read and written with pdfium on <see cref="PdfiumWorker.Shared"/>.</summary>
public sealed class LocalPdfBackend : IPdfBackend
{
    public bool Exists(string path) => File.Exists(path);

    public bool CanOverwrite(string path) => true;

    public long? FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : null;

    public async Task<IPdfSource> OpenAsync(string path, CancellationToken token = default) =>
        await PdfSource.OpenAsync(path, null, token).ConfigureAwait(false);

    public async Task<IPdfSource> CreateBlankAsync(PageSizePt size, CancellationToken token = default)
    {
        var folder = AppInfo.TempFolder;
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"blank-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, BlankPdf.Create(size), token).ConfigureAwait(false);
        PdfSource source;
        try
        {
            source = await PdfSource.OpenAsync(path, null, token).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        source.DeleteFileOnDispose = true;
        await source.LoadPageSizesAsync(null, token).ConfigureAwait(false);
        return source;
    }

    public async Task<(IPdfSource Saved, int ReducedImages)> SaveAsync(IReadOnlyList<PageEntry> pages, IPdfSource baseSource, string path,
        IReadOnlyList<IPdfSource> openSources, IProgress<double>? progress = null, CancellationToken token = default,
        ImageReduction? reduceImages = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var local = PdfSource.Local(baseSource);
        var (temp, reduced) = await PdfSaver.WriteDocumentAsync(pages, baseSource, path, progress, token, reduceImages).ConfigureAwait(false);
        if (token.IsCancellationRequested)
        {
            TryDelete(temp);
            token.ThrowIfCancellationRequested();
        }

        // The target may be open (overwriting the original): release those handles for the replace
        var affected = new List<PdfSource>();
        foreach (var s in openSources)
            if (s is PdfSource p && EditableDocument.PathsEqual(p.Path, path))
                affected.Add(p);
        foreach (var s in affected)
            await s.CloseAsync().ConfigureAwait(false);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            foreach (var s in affected)
                await s.ReopenAsync().ConfigureAwait(false);
            TryDelete(temp);
            throw;
        }

        var fresh = await PdfSource.OpenAsync(path, local.Worker, token).ConfigureAwait(false);
        // All sizes before the views lay out again: unknown ones would take the first page's size (landscape pages after a
        // portrait first page came out portrait and distorted). No token: the file is already replaced.
        await fresh.LoadPageSizesAsync().ConfigureAwait(false);
        return (fresh, reduced);
    }

    public Task ExtractAsync(IReadOnlyList<PageEntry> pages, string targetPath, IProgress<double>? progress = null, CancellationToken token = default) =>
        PdfSaver.ExtractAsync(pages, targetPath, null, progress, token);

    public async Task<int> SplitAsync(IReadOnlyList<PageEntry> pages, IReadOnlyList<SplitPart> parts, string target, string baseName,
        IProgress<double>? progress = null, CancellationToken token = default) =>
        (await PdfSaver.SplitAsync(pages, parts, target, baseName, progress, token).ConfigureAwait(false)).Count;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

/// <summary>A minimal PDF with one empty page (blank pages are inserted as pages of such a file).</summary>
public static class BlankPdf
{
    public static byte[] Create(PageSizePt size)
    {
        string F(double v) => Math.Max(1, Math.Round(v, 2)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        string[] objects =
        [
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {F(size.Width)} {F(size.Height)}]>>",
        ];
        var sb = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<</Size {objects.Length + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
