using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using PDFiumCore;
using SkiaSharp;
using Xunit;

namespace EasyPDF.Core.Tests;

/// <summary>Saving with <see cref="ImageReduction"/>: scans and pictures are scaled down, the pages look the same.</summary>
public class ImageReductionTests
{
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static async Task<EditableDocument> OpenAsync(string path)
    {
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    /// <summary>A photo-like JPEG: smooth colour gradients with fine noise (compresses like a real scan, not like a flat colour).</summary>
    private static byte[] Jpeg(int width, int height, bool gray, int quality = 92)
    {
        var random = new Random(1);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, gray ? SKColorType.Gray8 : SKColorType.Bgra8888, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var noise = random.Next(-12, 13);
                var r = (byte)Math.Clamp(255 * x / width + noise, 0, 255);
                var g = (byte)Math.Clamp(255 * y / height + noise, 0, 255);
                if (gray)
                    pixels[y * rowBytes + x] = (byte)((r + g) / 2);
                else
                {
                    var i = y * rowBytes + x * 4;
                    pixels[i] = 128;
                    pixels[i + 1] = g;
                    pixels[i + 2] = r;
                    pixels[i + 3] = 255;
                }
            }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    /// <summary>Pixel size and colour space of every picture on each page of a saved file.</summary>
    private static Task<List<(int Width, int Height, int ColorSpace)>[]> PicturesAsync(string path) => PdfiumWorker.Shared.Run(() =>
    {
        var doc = fpdfview.FPDF_LoadDocument(path, null);
        Assert.NotNull(doc);
        try
        {
            var result = new List<(int, int, int)>[fpdfview.FPDF_GetPageCount(doc)];
            for (var p = 0; p < result.Length; p++)
            {
                result[p] = [];
                var page = fpdfview.FPDF_LoadPage(doc, p);
                for (var i = 0; i < fpdf_edit.FPDFPageCountObjects(page); i++)
                {
                    var obj = fpdf_edit.FPDFPageGetObject(page, i);
                    if (fpdf_edit.FPDFPageObjGetType(obj) != 3)
                        continue;
                    using var meta = new FPDF_IMAGEOBJ_METADATA();
                    fpdf_edit.FPDFImageObjGetImageMetadata(obj, page, meta);
                    result[p].Add(((int)meta.Width, (int)meta.Height, meta.Colorspace));
                }
                fpdfview.FPDF_ClosePage(page);
            }
            return result;
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(doc);
        }
    });

    private static async Task<RenderedPage> RenderAsync(string path, int page = 0)
    {
        await using var source = await PdfSource.OpenAsync(path);
        return await source.RenderAsync(page, 40, 40);
    }

    [Fact]
    public async Task ScansAreScaledDownToTheChosenResolutionAndLookTheSame()
    {
        using var dir = new TempDir();
        var original = dir.File("scan.pdf");
        TestPdf.WithJpeg(original, Jpeg(1200, 1200, gray: false), 1200, 1200, gray: false, 288, 288); // 4 x 4 inches: 300 dpi
        var before = await RenderAsync(original);

        await using (var doc = await OpenAsync(original))
        {
            var progress = new List<double>();
            var reduced = await doc.SaveAsync(dir.File("small.pdf"), new SyncProgress(progress.Add), default, ImageReduction.Medium);
            Assert.Equal(1, reduced);
            Assert.Equal(1, progress[^1]);
            Assert.Equal(progress.Order(), progress);
            Assert.False(doc.IsDirty);
            Assert.Equal(dir.File("small.pdf"), doc.FilePath);
        }

        var picture = Assert.Single((await PicturesAsync(dir.File("small.pdf")))[0]);
        Assert.Equal((600, 600, 2), picture);                                              // 150 dpi, still RGB
        Assert.True(new FileInfo(dir.File("small.pdf")).Length < new FileInfo(original).Length / 2);

        var after = await RenderAsync(dir.File("small.pdf"));
        var difference = before.Pixels.Zip(after.Pixels, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(difference < 24, $"Largest colour difference {difference}");
    }

    /// <summary>
    /// The page content is never written again: pdfium's content generator once turned a box in a spot colour black and
    /// dropped its white text (seen on the cover of the demo PDF).
    /// </summary>
    [Fact]
    public async Task TheRestOfThePageStaysUntouched()
    {
        using var dir = new TempDir();
        var original = dir.File("cover.pdf");
        TestPdf.WithJpeg(original, Jpeg(1200, 1200, gray: false), 1200, 1200, gray: false, 288, 288,
            extraContent: "/CS0 cs 1 scn 40 40 208 100 re f BT /F1 36 Tf 1 1 1 rg 50 70 Td (TITLE) Tj ET",
            extraResources: "/ColorSpace<</CS0[/Separation/Blue/DeviceRGB<</FunctionType 2/Domain[0 1]/C0[1 1 1]/C1[0 0.6 0.8]/N 1>>]>>" +
                            "/Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>>>");
        await using (var doc = await OpenAsync(original))
            Assert.Equal(1, await doc.SaveAsync(dir.File("small.pdf"), null, default, ImageReduction.Medium));

        await using var before = await PdfSource.OpenAsync(original);
        await using var after = await PdfSource.OpenAsync(dir.File("small.pdf"));
        var a = await before.RenderAsync(0, 144, 144);
        var b = await after.RenderAsync(0, 144, 144);
        // The box (y 40..140 pt from the bottom = rows 74..124 at half scale) in blue and the white text in it
        var box = Enumerable.Range(76, 46).SelectMany(y => Enumerable.Range(22, 100).Select(x => y * a.Stride + x * 4)).ToList();
        int Text(RenderedPage p) => box.Count(i => p.Pixels[i + 2] > 150); // the box has no red, the white text has
        Assert.True(Text(a) > 30);
        Assert.InRange(Text(b), Text(a) * 0.9, Text(a) * 1.1);
        Assert.Equal(box.Count(i => a.Pixels[i] > 150 && a.Pixels[i + 2] < 60), box.Count(i => b.Pixels[i] > 150 && b.Pixels[i + 2] < 60));
    }

    [Fact]
    public async Task GreyScansStayGrey()
    {
        using var dir = new TempDir();
        var original = dir.File("scan.pdf");
        TestPdf.WithJpeg(original, Jpeg(1200, 1600, gray: true), 1200, 1600, gray: true, 288, 384);
        await using (var doc = await OpenAsync(original))
            Assert.Equal(1, await doc.SaveAsync(original, null, default, ImageReduction.Small));   // overwriting the open file

        Assert.Equal((400, 533, 1), Assert.Single((await PicturesAsync(original))[0]));          // 100 dpi, DeviceGray
    }

    [Fact]
    public async Task APictureUsedOnSeveralPagesIsReducedOnce()
    {
        using var dir = new TempDir();
        var original = dir.File("scan.pdf");
        TestPdf.WithJpeg(original, Jpeg(1200, 1200, gray: false), 1200, 1200, gray: false, 288, 288, pageCount: 3);
        await using (var doc = await OpenAsync(original))
            Assert.Equal(1, await doc.SaveAsync(dir.File("small.pdf"), null, default, ImageReduction.Medium));

        var pages = await PicturesAsync(dir.File("small.pdf"));
        Assert.All(pages, p => Assert.Equal((600, 600, 2), Assert.Single(p)));
        // Neither the old picture nor three copies of the new one are in the file
        Assert.True(new FileInfo(dir.File("small.pdf")).Length < new FileInfo(original).Length / 10);
    }

    [Fact]
    public async Task PicturesAtLowResolutionOrWithTransparencyStayAsTheyAre()
    {
        using var dir = new TempDir();
        // 160 dpi is within the tolerance above 150 dpi
        var low = dir.File("low.pdf");
        TestPdf.WithJpeg(low, Jpeg(640, 640, gray: false), 640, 640, gray: false, 288, 288);
        // A soft mask (object 6) would be lost in a JPEG
        var masked = dir.File("masked.pdf");
        TestPdf.WithJpeg(masked, Jpeg(1200, 1200, gray: false), 1200, 1200, gray: false, 288, 288, imageExtras: "/SMask 6 0 R",
            extraObjects: ["<</Type/XObject/Subtype/Image/Width 1/Height 1/ColorSpace/DeviceGray/BitsPerComponent 8/Length 1>>\nstream\n\x80\nendstream"]);

        foreach (var (path, pixels) in new[] { (low, 640), (masked, 1200) })
        {
            await using (var doc = await OpenAsync(path))
                Assert.Equal(0, await doc.SaveAsync(dir.File("out.pdf"), null, default, ImageReduction.Medium));
            Assert.Equal(pixels, Assert.Single((await PicturesAsync(dir.File("out.pdf")))[0]).Width);
        }
    }

    [Fact]
    public async Task SavingWithoutReductionKeepsThePictures()
    {
        using var dir = new TempDir();
        var original = dir.File("scan.pdf");
        TestPdf.WithJpeg(original, Jpeg(1200, 1200, gray: false), 1200, 1200, gray: false, 288, 288);
        await using (var doc = await OpenAsync(original))
            Assert.Equal(0, await doc.SaveAsync(dir.File("copy.pdf")));
        Assert.Equal(1200, Assert.Single((await PicturesAsync(dir.File("copy.pdf")))[0]).Width);
    }
}
