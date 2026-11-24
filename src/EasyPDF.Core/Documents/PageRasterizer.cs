using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Pdfium;
using PDFiumCore;
using SkiaSharp;

namespace EasyPDF.Core.Documents;

/// <summary>How "Save as image" turns the pages into pictures.</summary>
/// <param name="Dpi">Resolution of the page pictures (dots per inch).</param>
/// <param name="Grayscale">Grey pictures instead of colour ones (much smaller files).</param>
public sealed record PageRasterization(int Dpi, bool Grayscale)
{
    public static int[] DpiChoices { get; } = [150, 200, 300];

    public static PageRasterization Default { get; } = new(200, false);
}

/// <summary>
/// "Save as image": replaces a PDF that pdfium has just written by a new document whose pages are only pictures of the
/// pages as they look (content, comments, stamps, form values), so no text, font, form field, link or comment is left that
/// could be changed, copied or searched. The new document is built from scratch: nothing of the old file is copied
/// (bookmarks, metadata, attachments, forms and scripts are left out on purpose; they could carry the old text). The page
/// pictures are JPEGs (colour, or grey with one channel).
/// </summary>
public static class PageRasterizer
{
    private const int JpegQuality = 85;

    /// <summary>A page picture never has more pixels (a huge page at 300 dpi gets a lower resolution instead).</summary>
    private const double MaxPixels = 60_000_000;

    private const int FpdfAnnot = 0x01;

    /// <summary>Replaces the PDF at <paramref name="path"/> by its pages as pictures (in place).</summary>
    public static async Task RasterizeFileAsync(PdfiumWorker worker, string path, PageRasterization settings, IProgress<double>? progress,
        CancellationToken token)
    {
        var rewritten = $"{path}.{Guid.NewGuid():N}.tmp";
        FpdfDocumentT? target = null;
        try
        {
            await using (var source = await PdfSource.OpenAsync(path, worker, token).ConfigureAwait(false))
            {
                target = await worker.Run(() => fpdf_edit.FPDF_CreateNewDocument(), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A new document could not be created.");
                for (var i = 0; i < source.PageCount; i++)
                {
                    var index = i;
                    var (width, height, jpeg) = await worker.Run(() => RenderPage(source.Handle, index, settings), PdfiumWorker.PriorityBackground, token)
                        .ConfigureAwait(false);
                    var doc = target;
                    await worker.Run(() => AddPage(doc, index, width, height, jpeg), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
                    progress?.Report(0.95 * (i + 1) / source.PageCount);
                }
            }
            var written = target;
            await worker.Run(() => PdfSaver.SaveToFile(written, rewritten), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(rewritten, path, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            if (target != null)
            {
                var doc = target;
                await worker.Run(() => fpdfview.FPDF_CloseDocument(doc), PdfiumWorker.PriorityBackground, CancellationToken.None).ConfigureAwait(false);
            }
            try
            {
                File.Delete(rewritten);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The page as shown (its rotation applied), rendered with its comments on white and encoded as JPEG; size in points.</summary>
    private static (double Width, double Height, byte[] Jpeg) RenderPage(FpdfDocumentT doc, int index, PageRasterization settings)
    {
        var page = fpdfview.FPDF_LoadPage(doc, index) ?? throw new InvalidOperationException($"Page {index + 1} could not be loaded.");
        try
        {
            double width = fpdfview.FPDF_GetPageWidthF(page), height = fpdfview.FPDF_GetPageHeightF(page);
            var scale = settings.Dpi / 72.0;
            var pixels = width * scale * height * scale;
            if (pixels > MaxPixels)
                scale *= Math.Sqrt(MaxPixels / pixels);
            int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
            var bitmap = fpdfview.FPDFBitmapCreate(w, h, 0) ?? throw new OutOfMemoryException("Bitmap allocation failed.");
            try
            {
                fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
                fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, FpdfAnnot);
                return (width, height, Encode(bitmap, w, h, settings.Grayscale));
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
            }
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>BGRx pixels as a JPEG: colour, or grey with one channel (luma as in JPEG/Rec. 601).</summary>
    internal static byte[] Encode(FpdfBitmapT bitmap, int w, int h, bool grayscale)
    {
        var stride = fpdfview.FPDFBitmapGetStride(bitmap);
        var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
        var info = grayscale
            ? new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque)
            : new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var skia = new SKBitmap(info);
        var target = skia.GetPixelSpan();
        var row = new byte[w * 4];
        for (var y = 0; y < h; y++)
        {
            Marshal.Copy(buffer + y * stride, row, 0, row.Length);
            var line = target.Slice(y * skia.RowBytes);
            if (grayscale)
            {
                for (var x = 0; x < w; x++)
                    line[x] = (byte)((row[x * 4 + 2] * 299 + row[x * 4 + 1] * 587 + row[x * 4] * 114 + 500) / 1000);
            }
            else
            {
                for (var x = 3; x < row.Length; x += 4)
                    row[x] = 255; // BGRx has no defined fourth byte
                row.CopyTo(line);
            }
        }
        using var image = SKImage.FromBitmap(skia);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
            ?? throw new InvalidOperationException("The page picture could not be encoded.");
        return data.ToArray();
    }

    /// <summary>A new page of the size in points holding only the picture, filling it.</summary>
    private static void AddPage(FpdfDocumentT doc, int index, double width, double height, byte[] jpeg)
    {
        var page = fpdf_edit.FPDFPageNew(doc, index, width, height) ?? throw new InvalidOperationException($"Page {index + 1} could not be created.");
        try
        {
            var image = fpdf_edit.FPDFPageObjNewImageObj(doc) ?? throw new InvalidOperationException("The picture could not be created.");
            PageRedactor.LoadJpeg(image, jpeg);
            if (fpdf_edit.FPDFImageObjSetMatrix(image, width, 0, 0, height, 0, 0) == 0)
            {
                fpdf_edit.FPDFPageObjDestroy(image);
                throw new InvalidOperationException("The picture could not be positioned.");
            }
            fpdf_edit.FPDFPageInsertObject(page, image);
            if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
                throw new InvalidOperationException($"Page {index + 1} could not be written.");
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }
}
