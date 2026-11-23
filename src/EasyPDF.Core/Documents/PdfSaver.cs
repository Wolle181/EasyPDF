using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PDFiumCore;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Core.Documents;

/// <summary>A run of consecutive pages that becomes one output file when splitting.</summary>
public readonly record struct SplitPart(int Start, int Count);

public static class SplitPlan
{
    /// <summary>Parts of <paramref name="n"/> pages each (the last part may be shorter).</summary>
    public static IReadOnlyList<SplitPart> EveryN(int pageCount, int n)
    {
        if (n < 1)
            throw new ArgumentOutOfRangeException(nameof(n));
        var parts = new List<SplitPart>();
        for (var start = 0; start < pageCount; start += n)
            parts.Add(new SplitPart(start, Math.Min(n, pageCount - start)));
        return parts;
    }

    /// <summary>Each of the given pages starts a new part; the document start always does.</summary>
    public static IReadOnlyList<SplitPart> AtPages(int pageCount, IEnumerable<int> startPages)
    {
        var starts = startPages.Where(i => i > 0 && i < pageCount).Append(0).Distinct().OrderBy(i => i).ToList();
        var parts = new List<SplitPart>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : pageCount;
            parts.Add(new SplitPart(starts[i], end - starts[i]));
        }
        return parts;
    }
}

/// <summary>
/// Writes PDFs with pdfium. Work is split into small worker jobs so rendering stays responsive,
/// except the final <c>FPDF_SaveAsCopy</c> which is one job.
/// </summary>
public static class PdfSaver
{
    /// <summary>Stamps need their decoded pixels to be written; refuse instead of silently dropping one.</summary>
    private static void RejectStamps(IReadOnlyList<PageEntry> pages)
    {
        foreach (var stamp in pages.SelectMany(p => p.Stamps))
            if (stamp.Bgra == null || stamp.Bgra.Length < (long)stamp.PixelWidth * stamp.PixelHeight * 4)
                throw new NotSupportedException("A placed stamp has no decoded picture and cannot be saved.");
    }

    private const int ImportBatch = 100;

    /// <summary>
    /// Saves the page list based on a fresh copy of <paramref name="baseSource"/> (keeps bookmarks, metadata, forms):
    /// pages of other files are imported, removed pages deleted, the rest reordered and rotated.
    /// With <paramref name="reduceImages"/> its pictures are scaled down (<see cref="ImageReducer"/>).
    /// Returns the path of the finished temp file, located in the folder of <paramref name="targetPath"/>, and how many
    /// pictures were scaled down.
    /// </summary>
    public static async Task<(string TempPath, int ReducedImages)> WriteDocumentAsync(IReadOnlyList<PageEntry> pages, IPdfSource baseSource,
        string targetPath, IProgress<double>? progress = null, CancellationToken token = default, ImageReduction? reduceImages = null)
    {
        if (pages.Count == 0)
            throw new InvalidOperationException("A document needs at least one page.");
        RejectStamps(pages);

        // Reducing pictures (after writing) takes the second half of the progress range
        var reduceShare = reduceImages == null ? 0 : 0.5;
        double At(double p) => p * (1 - reduceShare);
        var worker = PdfSource.Local(baseSource).Worker;
        var temp = TempPathFor(targetPath);
        await using var work = await PdfSource.OpenAsync(baseSource.Path, worker, token).ConfigureAwait(false);
        try
        {
            var baseCount = work.PageCount;
            var workIndex = new int[pages.Count];
            var foreign = new List<int>(); // positions in 'pages' that must be imported
            for (var i = 0; i < pages.Count; i++)
            {
                if (ReferenceEquals(pages[i].Source, baseSource))
                    workIndex[i] = pages[i].SourceIndex;
                else
                {
                    workIndex[i] = baseCount + foreign.Count;
                    foreign.Add(i);
                }
            }

            // 1. import foreign pages (appended after the base pages, in final relative order)
            for (var offset = 0; offset < foreign.Count; offset += ImportBatch)
            {
                var batch = foreign.Skip(offset).Take(ImportBatch).Select(i => pages[i]).ToList();
                await worker.Run(() =>
                {
                    foreach (var entry in batch)
                        ImportPage(work.Handle, PdfSource.Local(entry.Source).Handle, entry.SourceIndex, fpdfview.FPDF_GetPageCount(work.Handle));
                }, PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
                progress?.Report(At(0.4 * Math.Min(offset + ImportBatch, foreign.Count) / foreign.Count));
            }

            // 2. delete unused pages, then bring the rest into the target order
            await worker.Run(() =>
            {
                var doc = work.Handle;
                var total = baseCount + foreign.Count;
                var used = new bool[total];
                foreach (var w in workIndex)
                    used[w] = true;
                var compact = new int[total];
                var next = 0;
                for (var i = 0; i < total; i++)
                    compact[i] = used[i] ? next++ : -1;
                for (var i = total - 1; i >= 0; i--)
                    if (!used[i])
                        fpdf_edit.FPDFPageDelete(doc, i);

                var order = workIndex.Select(w => compact[w]).ToArray();
                if (!order.Select((v, i) => v == i).All(b => b))
                    fpdf_edit.FPDF_MovePages(doc, ref order[0], (ulong)order.Length, 0);
            }, PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            progress?.Report(At(0.6));

            // 3. overlays, redactions, text layers, rotation, 4. write
            await worker.Run(() => { ApplyStamps(work.Handle, pages); ApplyTexts(work.Handle, pages); }, PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            await ApplyRedactionsAsync(worker, work.Handle, pages, token).ConfigureAwait(false);
            await worker.Run(() => ApplyPastedAreas(work.Handle, pages), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            await ApplyOcrAsync(worker, work.Handle, pages, token).ConfigureAwait(false);
            await worker.Run(() => ApplyRotations(work.Handle, pages), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            progress?.Report(At(0.7));
            await worker.Run(() => SaveToFile(work.Handle, temp), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); // the final write is one job; a cancel during it is honoured here
            if (pages.Any(p => p.RemovalAreas.Count > 0))
                await DropUnusedObjectsAsync(worker, temp, token).ConfigureAwait(false);
            progress?.Report(At(1));

            // 5. scale down the pictures of the written file
            var reduced = reduceImages == null ? 0 : await ImageReducer.ReduceFileAsync(worker, temp, reduceImages,
                new RelayProgress(p => progress?.Report(At(1) + reduceShare * p)), token).ConfigureAwait(false);
            progress?.Report(1);
            return (temp, reduced);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Reports straight away (<see cref="Progress{T}"/> would post to the caller's context first).</summary>
    private sealed class RelayProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Saves the given pages (current order + rotation) as a new PDF without bookmarks.</summary>
    public static async Task ExtractAsync(IReadOnlyList<PageEntry> pages, string targetPath, PdfiumWorker? worker = null,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (pages.Count == 0)
            throw new InvalidOperationException("No pages to extract.");
        RejectStamps(pages);
        worker ??= PdfSource.Local(pages[0].Source).Worker;
        var temp = TempPathFor(targetPath);
        try
        {
            FpdfDocumentT doc = await worker.Run(() => fpdf_edit.FPDF_CreateNewDocument(), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            try
            {
                for (var offset = 0; offset < pages.Count; offset += ImportBatch)
                {
                    var batch = pages.Skip(offset).Take(ImportBatch).ToList();
                    await worker.Run(() =>
                    {
                        foreach (var entry in batch)
                            ImportPage(doc, PdfSource.Local(entry.Source).Handle, entry.SourceIndex, fpdfview.FPDF_GetPageCount(doc));
                    }, PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
                    progress?.Report(0.8 * Math.Min(offset + ImportBatch, pages.Count) / pages.Count);
                }
                await worker.Run(() => { ApplyStamps(doc, pages); ApplyTexts(doc, pages); }, PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
                await ApplyRedactionsAsync(worker, doc, pages, token).ConfigureAwait(false);
                await worker.Run(() => ApplyPastedAreas(doc, pages), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
                await ApplyOcrAsync(worker, doc, pages, token).ConfigureAwait(false);
                await worker.Run(() =>
                {
                    ApplyRotations(doc, pages);
                    SaveToFile(doc, temp);
                }, PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            }
            finally
            {
                await worker.Run(() => fpdfview.FPDF_CloseDocument(doc), PdfiumWorker.PriorityVisible).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (pages.Any(p => p.RemovalAreas.Count > 0))
                await DropUnusedObjectsAsync(worker, temp, token).ConfigureAwait(false);
            File.Move(temp, targetPath, overwrite: true);
            progress?.Report(1);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>File name of a split part, e.g. "Manual_part03.pdf" (<paramref name="index"/> is zero-based).</summary>
    public static string PartFileName(string baseName, int index, int partCount)
    {
        var digits = Math.Max(2, partCount.ToString().Length);
        return $"{baseName}_part{(index + 1).ToString().PadLeft(digits, '0')}.pdf";
    }

    /// <summary>Writes each part to <c>&lt;baseName&gt;_partNN.pdf</c> in <paramref name="folder"/>; returns the file paths.</summary>
    public static async Task<IReadOnlyList<string>> SplitAsync(IReadOnlyList<PageEntry> pages, IReadOnlyList<SplitPart> parts,
        string folder, string baseName, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var files = new List<string>();
        try
        {
            for (var i = 0; i < parts.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(folder, PartFileName(baseName, i, parts.Count));
                var slice = pages.Skip(parts[i].Start).Take(parts[i].Count).ToList();
                await ExtractAsync(slice, path, null, null, token).ConfigureAwait(false);
                files.Add(path);
                progress?.Report((i + 1.0) / parts.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled split leaves nothing behind
            foreach (var file in files)
                TryDelete(file);
            throw;
        }
        return files;
    }

    /// <summary>Writes the text layers page by page (a page can hold hundreds of words; other jobs can run in between).</summary>
    private static async Task ApplyOcrAsync(PdfiumWorker worker, FpdfDocumentT doc, IReadOnlyList<PageEntry> pages, CancellationToken token)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].OcrWords.Count == 0)
                continue;
            var index = i;
            await worker.Run(() => ApplyOcr(doc, index, pages[index]), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes what lies under the redaction boxes and the pasted areas, page by page (after stamps and texts, so they are
    /// removed as well, before the pasted pictures, which go on top, and the recognized words, which are written only
    /// where no box is).
    /// </summary>
    private static async Task ApplyRedactionsAsync(PdfiumWorker worker, FpdfDocumentT doc, IReadOnlyList<PageEntry> pages, CancellationToken token)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].RemovalAreas.Count == 0)
                continue;
            var index = i;
            await worker.Run(() => PageRedactor.Apply(doc, index, pages[index]), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the file at <paramref name="path"/> once more with pdfium. pdfium leaves out objects of a file it opened that
    /// nothing refers to any more, but writes every object created while editing, used or not: the content of a
    /// redacted page that was imported (other file, extract) or got stamps would still be in the file. Opened again,
    /// all objects are old ones, and the unused ones are dropped (RedactionTests).
    /// </summary>
    private static async Task DropUnusedObjectsAsync(PdfiumWorker worker, string path, CancellationToken token)
    {
        var rewritten = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var written = await PdfSource.OpenAsync(path, worker, token).ConfigureAwait(false))
                await worker.Run(() => SaveToFile(written.Handle, rewritten), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(rewritten, path, overwrite: true);
        }
        finally
        {
            TryDelete(rewritten);
        }
    }

    #region pdfium helpers (worker thread only)

    // Stamps, texts and text layers only insert new objects. FPDFPage_GenerateContent then writes them into a stream of
    // their own and leaves the existing streams as they are; never change existing page objects before calling it,
    // because a rewritten stream can lose spot colours and text (OverlayContentTests). Redactions are the exception:
    // they remove the objects under their boxes on purpose (PageRedactor).

    private static void ImportPage(FpdfDocumentT dest, FpdfDocumentT source, int sourceIndex, int insertAt)
    {
        var index = sourceIndex;
        if (fpdf_ppo.FPDF_ImportPagesByIndex(dest, source, ref index, 1, insertAt) == 0)
            throw new InvalidOperationException($"Page {sourceIndex + 1} could not be imported.");
    }

    /// <summary>
    /// Writes the placed stamps into the page content as image objects (before the extra rotation is applied,
    /// because stamps live in the page space before it). Pasted areas come later (<see cref="ApplyPastedAreas"/>).
    /// </summary>
    private static void ApplyStamps(FpdfDocumentT doc, IReadOnlyList<PageEntry> pages)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            if (!pages[i].Stamps.Any(s => !s.CoversContent))
                continue;
            var page = fpdfview.FPDF_LoadPage(doc, i);
            if (page == null)
                throw new InvalidOperationException($"Page {i + 1} could not be loaded.");
            try
            {
                var size = pages[i].BaseSize;
                foreach (var stamp in pages[i].Stamps.Where(s => !s.CoversContent))
                    AddImage(doc, page, size, stamp);
                if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
                    throw new InvalidOperationException($"The stamps of page {i + 1} could not be written.");
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Writes the pasted areas on top, after the content under them was removed (<see cref="ApplyRedactionsAsync"/>).
    /// Redaction boxes reaching into a pasted area are painted into its pixels: on screen they lie above it, and what
    /// they hide must not stay in the file inside the picture.
    /// </summary>
    private static void ApplyPastedAreas(FpdfDocumentT doc, IReadOnlyList<PageEntry> pages)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            if (!pages[i].Stamps.Any(s => s.CoversContent))
                continue;
            var page = fpdfview.FPDF_LoadPage(doc, i);
            if (page == null)
                throw new InvalidOperationException($"Page {i + 1} could not be loaded.");
            try
            {
                var size = pages[i].BaseSize;
                foreach (var stamp in pages[i].Stamps.Where(s => s.CoversContent))
                    AddImage(doc, page, size, PaintRedactions(stamp, pages[i].Redactions));
                if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
                    throw new InvalidOperationException($"The pasted areas of page {i + 1} could not be written.");
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// The stamp with the parts of its picture under the redaction boxes filled in their colour (a copy; the stamp itself
    /// if no box reaches into it).
    /// </summary>
    internal static StampOverlay PaintRedactions(StampOverlay stamp, IReadOnlyList<RedactionOverlay> redactions)
    {
        var hits = redactions.Where(h => Redaction.Intersects(h.Rect, stamp.Rect)).ToList();
        if (hits.Count == 0 || stamp.Bgra == null)
            return stamp;
        var pixels = (byte[])stamp.Bgra.Clone();
        var r = stamp.Rect;
        // The picture is turned clockwise by Rotation into Rect: its own, unturned size in points
        var picture = new PageSizePt(r.Width, r.Height).Rotated(stamp.Rotation);
        double sx = stamp.PixelWidth / picture.Width, sy = stamp.PixelHeight / picture.Height;
        foreach (var hit in hits)
        {
            var own = PageGeometry.Unrotate(new RectPt(hit.Rect.X - r.X, hit.Rect.Y - r.Y, hit.Rect.Width, hit.Rect.Height), picture, stamp.Rotation);
            int x0 = Math.Max(0, (int)Math.Floor(own.X * sx)), y0 = Math.Max(0, (int)Math.Floor(own.Y * sy));
            int x1 = Math.Min(stamp.PixelWidth, (int)Math.Ceiling(own.Right * sx)), y1 = Math.Min(stamp.PixelHeight, (int)Math.Ceiling(own.Bottom * sy));
            var level = hit.Fill == RedactionFill.White ? (byte)255 : (byte)0;
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var p = (y * stamp.PixelWidth + x) * 4;
                    pixels[p] = pixels[p + 1] = pixels[p + 2] = level;
                    pixels[p + 3] = 255;
                }
        }
        return stamp with { Bgra = pixels };
    }

    /// <summary>
    /// Writes the placed texts as real PDF text objects (one object per line), before the extra rotation: standard fonts
    /// as they are, installed fonts embedded (each font file once per document).
    /// </summary>
    private static void ApplyTexts(FpdfDocumentT doc, IReadOnlyList<PageEntry> pages)
    {
        var embedded = new Dictionary<string, FpdfFontT?>();
        try
        {
            for (var i = 0; i < pages.Count; i++)
            {
                if (pages[i].Texts.Count == 0)
                    continue;
                var page = fpdfview.FPDF_LoadPage(doc, i);
                if (page == null)
                    throw new InvalidOperationException($"Page {i + 1} could not be loaded.");
                try
                {
                    foreach (var text in pages[i].Texts)
                        AddText(doc, page, pages[i].BaseSize, text, embedded);
                    if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
                        throw new InvalidOperationException($"The texts of page {i + 1} could not be written.");
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }
        }
        finally
        {
            foreach (var font in embedded.Values)
                if (font != null)
                    fpdf_edit.FPDFFontClose(font);
        }
    }

    // pdfium embeds the whole font file; larger ones (e.g. Asian fonts) would make every saved file that big
    private const long MaxEmbeddedFontBytes = 30L * 1024 * 1024;
    private const int FontTypeTrueType = 2; // FPDF_FONT_TRUETYPE

    /// <summary>The installed font of an overlay loaded into the document (once per file), or null when it is missing or cannot be embedded.</summary>
    private static unsafe (FpdfFontT Font, double Ascent)? EmbeddedFont(FpdfDocumentT doc, TextOverlay text, Dictionary<string, FpdfFontT?> embedded)
    {
        if (Fonts.FontLibrary.Shared.Find(text.Font, text.Bold, text.Italic) is not { } face)
            return null;
        if (!embedded.TryGetValue(face.Path, out var font))
        {
            font = null;
            try
            {
                var info = new FileInfo(face.Path);
                if (info.Length <= MaxEmbeddedFontBytes)
                {
                    var bytes = File.ReadAllBytes(face.Path);
                    fixed (byte* p = bytes)
                        font = fpdf_edit.FPDFTextLoadFont(doc, p, (uint)bytes.Length, FontTypeTrueType, 1);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                font = null;
            }
            embedded[face.Path] = font;
        }
        return font == null ? null : (font, face.Ascent);
    }

    internal static (double X, double Y) ToUser(FpdfPageT page, PageSizePt size, double x, double y)
    {
        double ux = 0, uy = 0;
        fpdfview.FPDF_DeviceToPage(page, 0, 0, (int)Math.Round(size.Width * DeviceScale), (int)Math.Round(size.Height * DeviceScale), 0,
            (int)Math.Round(x * DeviceScale), (int)Math.Round(y * DeviceScale), ref ux, ref uy);
        return (ux, uy);
    }

    private static void AddText(FpdfDocumentT doc, FpdfPageT page, PageSizePt size, TextOverlay text, Dictionary<string, FpdfFontT?> embedded)
    {
        // An installed font is embedded; a standard font, or an installed one missing here, is written as standard font
        var installed = Settings.TextStyle.IsStandard(text.Font) ? null : EmbeddedFont(doc, text, embedded);
        var font = Settings.TextStyle.IsStandard(text.Font) ? Settings.TextStyle.NormalizeFont(text.Font) : Settings.TextStyle.SimilarStandard(text.Font);
        var pdfFont = Settings.TextStyle.StandardPdfName(font, text.Bold, text.Italic);
        var ascent = installed?.Ascent ?? font switch
        {
            "Times" => 0.805,
            "Courier" => 0.735,
            _ => 0.81,
        }; // ascent = share of the line height above the baseline (matches the on-screen fonts)
        var (r, g, b) = Settings.TextStyle.ToRgb(text.Color);
        var lines = text.Text.Replace("\r\n", "\n").Split('\n');
        var (origin, xAxis, downAxis, _, localH) = TextFrame(page, size, text.Rect, text.Rotation);
        var lineHeight = localH / lines.Length;

        for (var n = 0; n < lines.Length; n++)
        {
            if (lines[n].Length == 0)
                continue;
            var obj = installed is { } face
                ? fpdf_edit.FPDFPageObjCreateTextObj(doc, face.Font, (float)text.FontSize)
                : fpdf_edit.FPDFPageObjNewTextObj(doc, pdfFont, (float)text.FontSize);
            if (obj == null)
                throw new InvalidOperationException("The text could not be created.");
            var chars = (lines[n] + "\0").Select(ch => (ushort)ch).ToArray();
            if (fpdf_edit.FPDFTextSetText(obj, ref chars[0]) == 0)
                throw new InvalidOperationException("The text could not be set.");
            fpdf_edit.FPDFPageObjSetFillColor(obj, r, g, b, 255);
            var baseline = n * lineHeight + lineHeight * ascent;
            // Up in the text is against the down axis
            fpdf_edit.FPDFPageObjTransform(obj, xAxis.X, xAxis.Y, -downAxis.X, -downAxis.Y,
                origin.X + downAxis.X * baseline, origin.Y + downAxis.Y * baseline);
            fpdf_edit.FPDFPageInsertObject(page, obj);
        }
    }

    /// <summary>
    /// The text-local frame of a rect in page space whose text is turned clockwise by <paramref name="rotation"/>:
    /// top-left corner of the unrotated text block, unit axes "right" and "down" of the text, all in user space,
    /// and the block's width and height along those axes.
    /// </summary>
    private static ((double X, double Y) Origin, (double X, double Y) XAxis, (double X, double Y) DownAxis, double Width, double Height)
        TextFrame(FpdfPageT page, PageSizePt size, RectPt rect, int rotation)
    {
        var steps = (((rotation % 360) + 360) % 360) / 90;
        (double X, double Y)[] shown = [(rect.X, rect.Y), (rect.Right, rect.Y), (rect.Right, rect.Bottom), (rect.X, rect.Bottom)];
        var user = new (double X, double Y)[4];
        for (var c = 0; c < 4; c++)
        {
            var p = shown[(c + steps) % 4];
            user[c] = ToUser(page, size, p.X, p.Y);
        }
        var xAxis = Unit(user[1].X - user[0].X, user[1].Y - user[0].Y);
        var downAxis = Unit(user[3].X - user[0].X, user[3].Y - user[0].Y);
        return steps % 2 == 0
            ? (user[0], xAxis, downAxis, rect.Width, rect.Height)
            : (user[0], xAxis, downAxis, rect.Height, rect.Width);
    }

    /// <summary>
    /// Writes the recognized words of one page as invisible text (render mode 3, like OCRmyPDF and scanners do), before
    /// the extra rotation: one Helvetica text object per word, stretched to the word's box. The page looks the same,
    /// but viewers can search, select and copy the text.
    /// </summary>
    private static void ApplyOcr(FpdfDocumentT doc, int index, PageEntry entry)
    {
        var page = fpdfview.FPDF_LoadPage(doc, index);
        if (page == null)
            throw new InvalidOperationException($"Page {index + 1} could not be loaded.");
        try
        {
            foreach (var word in Redaction.VisibleWords(entry.OcrWords, entry.RemovalAreas))
                AddInvisibleWord(doc, page, entry.BaseSize, word);
            if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
                throw new InvalidOperationException($"The text layer of page {index + 1} could not be written.");
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    // OCR boxes are about one line high; Helvetica glyphs of that size would stick out. Font size as share of the box
    // height and the baseline below the box centre (in font sizes) put the glyph boxes onto the printed word.
    private const double OcrFontShare = 0.6;
    private const double OcrBaselineBelowCenter = 0.3;

    internal static void AddInvisibleWord(FpdfDocumentT doc, FpdfPageT page, PageSizePt size, OcrWord word)
    {
        var text = ToWinAnsi(word.Text);
        if (text.Length == 0 || word.Box.Width <= 0 || word.Box.Height <= 0)
            return;
        var (origin, xAxis, downAxis, width, height) = TextFrame(page, size, word.Box, word.Rotation);
        var fontSize = height * OcrFontShare;
        var obj = fpdf_edit.FPDFPageObjNewTextObj(doc, "Helvetica", (float)fontSize);
        if (obj == null)
            throw new InvalidOperationException("The text could not be created.");
        void SetText(string s)
        {
            var chars = (s + "\0").Select(ch => (ushort)ch).ToArray();
            if (fpdf_edit.FPDFTextSetText(obj, ref chars[0]) == 0)
                throw new InvalidOperationException("The text could not be set.");
        }
        SetText(text);
        fpdf_edit.FPDFTextObjSetTextRenderMode(obj, FPDF_TEXT_RENDERMODE.FPDF_TEXTRENDERMODE_INVISIBLE);

        // Natural width of the word at this size, then stretch it horizontally onto the box
        float left = 0, bottom = 0, right = 0, top = 0;
        var natural = fpdf_edit.FPDFPageObjGetBounds(obj, ref left, ref bottom, ref right, ref top) != 0 ? right - left : 0;
        if (natural <= 0)
        {
            left = 0;
            natural = (float)(fontSize * 0.55 * text.Length);
        }
        // A trailing space (in the gap after the word): text extraction keeps the words apart, pdfium does not add
        // spaces between separate objects that are this close
        SetText(text + " ");
        var stretch = width / natural;
        var baseline = height / 2 + fontSize * OcrBaselineBelowCenter;
        var x = origin.X + downAxis.X * baseline - xAxis.X * left * stretch;
        var y = origin.Y + downAxis.Y * baseline - xAxis.Y * left * stretch;
        // Up in the text is against the down axis
        fpdf_edit.FPDFPageObjTransform(obj, xAxis.X * stretch, xAxis.Y * stretch, -downAxis.X, -downAxis.Y, x, y);
        fpdf_edit.FPDFPageInsertObject(page, obj);
    }

    private const string WinAnsiExtras = "€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ";

    /// <summary>The standard fonts are written WinAnsi-encoded (Latin-1 plus a few signs); other characters become '?'.</summary>
    public static string ToWinAnsi(string text)
    {
        var chars = text.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!((c >= 0x20 && c <= 0x7E) || (c >= 0xA0 && c <= 0xFF) || WinAnsiExtras.Contains(c)))
                chars[i] = '?';
        }
        return new string(chars);
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y);
        return length == 0 ? (1, 0) : (x / length, y / length);
    }

    private const double DeviceScale = 100; // device units per point, for FPDF_DeviceToPage's integer arguments

    private static void AddImage(FpdfDocumentT doc, FpdfPageT page, PageSizePt size, StampOverlay stamp)
    {
        var bgra = stamp.Bgra!;
        var bitmap = fpdfview.FPDFBitmapCreate(stamp.PixelWidth, stamp.PixelHeight, 1);
        if (bitmap == null)
            throw new OutOfMemoryException("Bitmap allocation failed.");
        try
        {
            var stride = fpdfview.FPDFBitmapGetStride(bitmap);
            var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
            for (var y = 0; y < stamp.PixelHeight; y++)
                System.Runtime.InteropServices.Marshal.Copy(bgra, y * stamp.PixelWidth * 4, buffer + y * stride, stamp.PixelWidth * 4);

            var image = fpdf_edit.FPDFPageObjNewImageObj(doc);
            if (image == null)
                throw new InvalidOperationException("The stamp could not be created.");
            if (fpdf_edit.FPDFImageObjSetBitmap(null!, 0, image, bitmap) == 0)
                throw new InvalidOperationException("The stamp picture could not be set.");

            // Corners of the rectangle as shown (top-left, top-right, bottom-right, bottom-left), mapped to page user space.
            // The picture's own corners (same order) land on those corners shifted by its clockwise rotation.
            var r = stamp.Rect;
            (double X, double Y)[] shown = [(r.X, r.Y), (r.Right, r.Y), (r.Right, r.Bottom), (r.X, r.Bottom)];
            var steps = (((stamp.Rotation % 360) + 360) % 360) / 90;
            var user = new (double X, double Y)[4];
            for (var c = 0; c < 4; c++)
            {
                var p = shown[(c + steps) % 4];
                double ux = 0, uy = 0;
                fpdfview.FPDF_DeviceToPage(page, 0, 0, (int)Math.Round(size.Width * DeviceScale), (int)Math.Round(size.Height * DeviceScale), 0,
                    (int)Math.Round(p.X * DeviceScale), (int)Math.Round(p.Y * DeviceScale), ref ux, ref uy);
                user[c] = (ux, uy);
            }
            // Unit square: (0,0) = bottom-left, (1,0) = bottom-right, (0,1) = top-left of the picture
            var bl = user[3];
            if (fpdf_edit.FPDFImageObjSetMatrix(image, user[2].X - bl.X, user[2].Y - bl.Y, user[0].X - bl.X, user[0].Y - bl.Y, bl.X, bl.Y) == 0)
                throw new InvalidOperationException("The stamp could not be positioned.");
            fpdf_edit.FPDFPageInsertObject(page, image);
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    private static void ApplyRotations(FpdfDocumentT doc, IReadOnlyList<PageEntry> pages)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            var extra = pages[i].ExtraRotation / 90;
            if (extra == 0)
                continue;
            var page = fpdfview.FPDF_LoadPage(doc, i);
            if (page == null)
                throw new InvalidOperationException($"Page {i + 1} could not be loaded.");
            fpdf_edit.FPDFPageSetRotation(page, (fpdf_edit.FPDFPageGetRotation(page) + extra) % 4);
            fpdfview.FPDF_ClosePage(page);
        }
    }

    internal static unsafe void SaveToFile(FpdfDocumentT doc, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        PDFiumCore.Delegates.Func_int___IntPtr___IntPtr_ulong writeBlock = (self, data, size) =>
        {
            try
            {
                stream.Write(new ReadOnlySpan<byte>((void*)data, (int)size));
                return 1;
            }
            catch (IOException)
            {
                return 0;
            }
        };
        var writer = new FPDF_FILEWRITE_ { Version = 1, WriteBlock = writeBlock };
        if (fpdf_save.FPDF_SaveAsCopy(doc, writer, 0) == 0)
            throw new IOException($"Writing '{path}' failed.");
        GC.KeepAlive(writeBlock);
        GC.KeepAlive(writer);
    }

    #endregion

    private static string TempPathFor(string targetPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(targetPath))!, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
