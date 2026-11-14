using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;
using SkiaSharp;
using Xunit;

namespace EasyPDF.Core.Tests;

/// <summary>
/// Stamps, texts and text layers are added to a page without touching what it already shows. pdfium's content generator
/// turns a box in a spot colour black and drops its text when it writes an existing content stream again (seen when
/// pictures were replaced in place); <c>FPDFPage_GenerateContent</c> only writes streams with changed objects, and new
/// objects go into a stream of their own, so adding objects must leave the page as it is.
/// </summary>
public class OverlayContentTests
{
    private static async Task<EditableDocument> OpenAsync(string path)
    {
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    private static byte[] Red(int w, int h) => [.. Enumerable.Range(0, w * h).SelectMany(_ => new byte[] { 0, 0, 255, 255 })];

    private static void AddAll(EditableDocument d, RectPt corner)
    {
        d.AddStamp(0, [1], 8, 4, corner, 0, Red(8, 4));
        d.AddText(0, "Note", "Helvetica", 10, "#C00000", corner);
        d.SetOcrWords(new Dictionary<int, IReadOnlyList<OcrWord>> { [d.Pages[0].Id] = [new OcrWord("Word", corner, 0.9f)] });
    }

    private static byte[] WhiteJpeg()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>
    /// Largest colour difference per pixel outside <paramref name="overlay"/> (rendered at 1 px per point); fails if the
    /// overlay itself is missing.
    /// </summary>
    private static async Task<int> DifferenceAsync(string a, string b, RectPt overlay)
    {
        await using var sa = await PdfSource.OpenAsync(a);
        await using var sb = await PdfSource.OpenAsync(b);
        await sa.LoadPageSizesAsync();
        var size = sa.GetPageSizeOrEstimate(0);
        int w = (int)size.Width, h = (int)size.Height;
        var ra = await sa.RenderAsync(0, w, h);
        var rb = await sb.RenderAsync(0, w, h);
        int max = 0, maxInside = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var inside = x >= overlay.X - 2 && x <= overlay.Right + 2 && y >= overlay.Y - 2 && y <= overlay.Bottom + 2;
                var i = y * ra.Stride + x * 4;
                for (var c = 0; c < 3; c++)
                {
                    var d = Math.Abs(ra.Pixels[i + c] - rb.Pixels[i + c]);
                    if (inside)
                        maxInside = Math.Max(maxInside, d);
                    else
                        max = Math.Max(max, d);
                }
            }
        Assert.True(maxInside > 100, $"The overlay is missing (difference {maxInside})");
        return max;
    }

    [Fact]
    public async Task ASpotColourBoxAndItsTextStayAsTheyAre()
    {
        using var dir = new TempDir();
        var original = dir.File("cover.pdf");
        TestPdf.WithJpeg(original, WhiteJpeg(), 8, 8, gray: false, 288, 288,
            extraContent: "/CS0 cs 1 scn 40 40 208 100 re f BT /F1 36 Tf 1 1 1 rg 50 70 Td (TITLE) Tj ET",
            extraResources: "/ColorSpace<</CS0[/Separation/Blue/DeviceRGB<</FunctionType 2/Domain[0 1]/C0[1 1 1]/C1[0 0.6 0.8]/N 1>>]>>" +
                            "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>>>");
        var corner = new RectPt(200, 10, 60, 20);
        await using (var d = await OpenAsync(original))
        {
            AddAll(d, corner);
            await d.SaveAsync(dir.File("saved.pdf"));
        }
        Assert.InRange(await DifferenceAsync(original, dir.File("saved.pdf"), corner), 0, 8);
    }

    [Fact]
    public async Task TheCoverOfTheDemoPdfStaysAsItIs()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "EasyPDF.slnx")))
            root = Path.GetDirectoryName(root);
        var demo = Path.Combine(root!, "demo-pdfs", "Annual Report 2025.pdf");
        Assert.True(File.Exists(demo), demo);
        using var dir = new TempDir();
        var original = dir.File("demo.pdf");
        File.Copy(demo, original);
        var corner = new RectPt(5, 5, 60, 20);
        await using (var d = await OpenAsync(original))
        {
            AddAll(d, corner);
            await PdfSaver.ExtractAsync([d.Pages[0]], dir.File("extract.pdf")); // before saving, which writes the overlays into the file
            await d.SaveAsync(dir.File("saved.pdf"));
        }
        Assert.InRange(await DifferenceAsync(original, dir.File("saved.pdf"), corner), 0, 8);
        Assert.InRange(await DifferenceAsync(original, dir.File("extract.pdf"), corner), 0, 8);
    }
}
