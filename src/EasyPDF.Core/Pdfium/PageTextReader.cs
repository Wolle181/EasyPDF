using System;
using System.Collections.Generic;
using System.Text;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using PDFiumCore;

namespace EasyPDF.Core.Pdfium;

/// <summary>Reads the text layer of a page with pdfium (worker thread only).</summary>
internal static class PageTextReader
{
    private const double DeviceScale = 100; // device units per point, for FPDF_PageToDevice's integer results

    public static int CountChars(FpdfDocumentT doc, int pageIndex)
    {
        var page = LoadPage(doc, pageIndex);
        try
        {
            var text = fpdf_text.FPDFTextLoadPage(page);
            if (text == null)
                return 0;
            try { return Math.Max(0, fpdf_text.FPDFTextCountChars(text)); }
            finally { fpdf_text.FPDFTextClosePage(text); }
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>The words of the page (split at white space) with boxes in the page's own space (after /Rotate, origin top-left, points).</summary>
    public static List<OcrWord> ReadWords(FpdfDocumentT doc, int pageIndex)
    {
        var words = new List<OcrWord>();
        var page = LoadPage(doc, pageIndex);
        try
        {
            var text = fpdf_text.FPDFTextLoadPage(page);
            if (text == null)
                return words;
            try
            {
                int w = (int)Math.Round(fpdfview.FPDF_GetPageWidthF(page) * DeviceScale), h = (int)Math.Round(fpdfview.FPDF_GetPageHeightF(page) * DeviceScale);
                var sb = new StringBuilder();
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                void Flush()
                {
                    if (sb.Length > 0)
                        words.Add(new OcrWord(sb.ToString(), new RectPt(x0, y0, x1 - x0, y1 - y0), 1));
                    sb.Clear();
                    x0 = y0 = double.MaxValue;
                    x1 = y1 = double.MinValue;
                }
                var count = fpdf_text.FPDFTextCountChars(text);
                for (var i = 0; i < count; i++)
                {
                    var u = fpdf_text.FPDFTextGetUnicode(text, i);
                    if (u <= 32 || u == 0xA0 || u == 0xFFFE || u == 0xFFFF)
                    {
                        Flush();
                        continue;
                    }
                    if (u == 2) // soft hyphen at a line end
                        continue;
                    double left = 0, right = 0, bottom = 0, top = 0;
                    if (fpdf_text.FPDFTextGetCharBox(text, i, ref left, ref right, ref bottom, ref top) != 0)
                    {
                        // Two opposite corners are enough: /Rotate turns in steps of 90°
                        foreach (var (px, py) in new[] { (left, top), (right, bottom) })
                        {
                            int dx = 0, dy = 0;
                            fpdfview.FPDF_PageToDevice(page, 0, 0, w, h, 0, px, py, ref dx, ref dy);
                            x0 = Math.Min(x0, dx / DeviceScale);
                            x1 = Math.Max(x1, dx / DeviceScale);
                            y0 = Math.Min(y0, dy / DeviceScale);
                            y1 = Math.Max(y1, dy / DeviceScale);
                        }
                    }
                    sb.Append(char.ConvertFromUtf32((int)u));
                }
                Flush();
            }
            finally
            {
                fpdf_text.FPDFTextClosePage(text);
            }
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
        // Words without any box (e.g. only generated characters) carry no position
        words.RemoveAll(word => word.Box.Width < 0 || word.Box.Height < 0);
        return words;
    }

    private static FpdfPageT LoadPage(FpdfDocumentT doc, int pageIndex) =>
        fpdfview.FPDF_LoadPage(doc, pageIndex) ?? throw new InvalidOperationException($"Page {pageIndex + 1} could not be loaded.");
}
