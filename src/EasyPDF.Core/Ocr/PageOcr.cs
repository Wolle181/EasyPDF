using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Core.Ocr;

/// <summary>Recognizes the text of PDF pages: render on the pdfium worker, recognize off it, map pixels to page points.</summary>
public static class PageOcr
{
    /// <summary>Render resolution. 300 dpi is the usual scan resolution; the recognizer crops lines from this image.</summary>
    public const double Dpi = 300;

    /// <summary>Upper limit for the longer image side, so posters and plans do not need gigabytes (A4 at 300 dpi: 3508).</summary>
    public const int MaxPixels = 7000;

    /// <summary>
    /// Words of the page in the page's own space (after its /Rotate, before the extra rotation, origin top-left, points),
    /// the same space as <see cref="TextOverlay"/> and <see cref="StampOverlay"/>.
    /// The page is recognized as shown, i.e. turned by <paramref name="extraRotation"/>: a sideways scan the user turned
    /// upright is read upright, and its words get the matching <see cref="OcrWord.Rotation"/>.
    /// </summary>
    public static async Task<IReadOnlyList<OcrWord>> RecognizeAsync(IOcrEngine engine, PdfSource source, int pageIndex,
        int extraRotation = 0, CancellationToken token = default)
    {
        extraRotation = ((extraRotation / 90 % 4) + 4) % 4 * 90;
        var size = await source.GetPageSizeAsync(pageIndex, token).ConfigureAwait(false);
        var shown = size.Rotated(extraRotation);
        var (width, height) = PixelSize(shown);
        var image = await source.RenderAsync(pageIndex, width, height, extraRotation, PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
        var words = await Task.Run(() => engine.Recognize(image, token), token).ConfigureAwait(false);
        double sx = shown.Width / width, sy = shown.Height / height;
        var rotation = (360 - extraRotation) % 360;
        return
        [
            .. words.Select(w => w with
            {
                Box = PageGeometry.Unrotate(new RectPt(w.Box.X * sx, w.Box.Y * sy, w.Box.Width * sx, w.Box.Height * sy), size, extraRotation),
                Rotation = rotation,
            }),
        ];
    }

    /// <summary>
    /// Recognizes the given pages one after the other. With <paramref name="skipPagesWithText"/> pages that already have
    /// a text layer (in the file, or recognized and not saved yet) are left out. Returns the words per
    /// <see cref="PageEntry.Id"/>; <paramref name="progress"/> gets the number of pages handled so far.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<OcrWord>>> RecognizePagesAsync(IOcrEngine engine,
        IReadOnlyList<PageEntry> pages, bool skipPagesWithText, IProgress<int>? progress = null, CancellationToken token = default)
    {
        var result = new Dictionary<int, IReadOnlyList<OcrWord>>();
        for (var i = 0; i < pages.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var page = pages[i];
            var skip = skipPagesWithText
                && (page.OcrWords.Count > 0 || await page.Source.CountTextCharsAsync(page.SourceIndex, token).ConfigureAwait(false) > 0);
            if (!skip)
                result[page.Id] = await RecognizeAsync(engine, page.Source, page.SourceIndex, page.ExtraRotation, token).ConfigureAwait(false);
            progress?.Report(i + 1);
        }
        return result;
    }

    /// <summary>Image size for a page at <see cref="Dpi"/>, limited to <see cref="MaxPixels"/> on the longer side.</summary>
    public static (int Width, int Height) PixelSize(PageSizePt size)
    {
        var scale = Math.Min(Dpi / 72, MaxPixels / Math.Max(1, Math.Max(size.Width, size.Height)));
        return (Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
    }
}
