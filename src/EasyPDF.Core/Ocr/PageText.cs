using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Core.Ocr;

/// <summary>Plain text of pages (copy to the clipboard): recognized words, or the text layer of the file.</summary>
public static class PageText
{
    /// <summary>
    /// The text of the pages, one after the other, separated by an empty line; pages without text are left out.
    /// A page's recognized words (not saved yet) win over the text layer of its file; words under a redaction are left out.
    /// <c>PageCount</c> is the number of pages that had text.
    /// </summary>
    public static async Task<(string Text, int PageCount)> ReadPagesAsync(IReadOnlyList<PageEntry> pages, CancellationToken token = default)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var page in pages)
        {
            var size = await page.Source.GetPageSizeAsync(page.SourceIndex, token).ConfigureAwait(false);
            var text = FromWords(await ReadWordsAsync(page, token).ConfigureAwait(false), size, page.ExtraRotation);
            if (text.Length == 0)
                continue;
            if (sb.Length > 0)
                sb.Append('\n').Append('\n');
            sb.Append(text);
            count++;
        }
        return (sb.ToString(), count);
    }

    /// <summary>
    /// The words of a page as they will be in the saved file: its recognized words (not saved yet) or else the text layer
    /// of its file, without the words under a redaction or a pasted area.
    /// </summary>
    public static async Task<IReadOnlyList<OcrWord>> ReadWordsAsync(PageEntry page, CancellationToken token = default)
    {
        var words = page.OcrWords.Count > 0
            ? page.OcrWords
            : await page.Source.ReadWordsAsync(page.SourceIndex, token).ConfigureAwait(false);
        return Redaction.VisibleWords(words, page.RemovalAreas);
    }

    /// <summary>
    /// Joins words to lines as the page is shown (turned by <paramref name="extraRotation"/>). The words keep their order
    /// (OCR delivers them line by line, a text layer in its own reading order); a word starts a new line when it does not
    /// continue the previous one to the right at the same height. A gap of more than a line height gives an empty line.
    /// </summary>
    /// <param name="pageSize">Page size in the words' space (after /Rotate, before the extra rotation).</param>
    public static string FromWords(IReadOnlyList<OcrWord> words, PageSizePt pageSize, int extraRotation = 0)
    {
        var sb = new StringBuilder();
        RectPt? previous = null;
        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word.Text))
                continue;
            var box = PageGeometry.Rotate(word.Box, pageSize, extraRotation);
            if (previous is { } p)
            {
                var height = Math.Max(1, Math.Min(p.Height, box.Height));
                var overlap = Math.Min(p.Y + p.Height, box.Y + box.Height) - Math.Max(p.Y, box.Y);
                if (overlap >= height * 0.5 && box.X >= p.X + p.Width - height)
                    sb.Append(' ');
                else
                {
                    sb.Append('\n');
                    if (box.Y - (p.Y + p.Height) > height)
                        sb.Append('\n');
                }
            }
            sb.Append(word.Text.Trim());
            previous = box;
        }
        return sb.ToString();
    }
}
