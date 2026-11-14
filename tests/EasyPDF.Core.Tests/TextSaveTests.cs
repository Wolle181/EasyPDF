using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

public class TextSaveTests
{
    private static async Task<EditableDocument> OpenAsync(TempDir dir)
    {
        var path = System.IO.Path.Combine(dir.Path, "a.pdf");
        TestPdf.Create(path, [(200, 100, 0), (200, 100, 0)]);
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    /// <summary>Bounding box (x0, y0, x1, y1) of the pixels that are not white, or null if the page is blank.</summary>
    private static async Task<(int X0, int Y0, int X1, int Y1, int Red)?> InkAsync(string path, int page)
    {
        await using var s = await PdfSource.OpenAsync(path);
        await s.LoadPageSizesAsync();
        var size = s.GetPageSizeOrEstimate(page);
        var img = await s.RenderAsync(page, (int)size.Width, (int)size.Height);
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, red = 0;
        for (var y = 0; y < (int)size.Height; y++)
            for (var x = 0; x < (int)size.Width; x++)
            {
                var i = y * img.Stride + x * 4;
                int b = img.Pixels[i], g = img.Pixels[i + 1], r = img.Pixels[i + 2];
                if (r > 200 && g > 200 && b > 200)
                    continue;
                if (r > 150 && g < 100 && b < 100)
                    red++;
                x0 = System.Math.Min(x0, x); y0 = System.Math.Min(y0, y);
                x1 = System.Math.Max(x1, x); y1 = System.Math.Max(y1, y);
            }
        return x1 < 0 ? null : (x0, y0, x1, y1, red);
    }

    [Fact]
    public async Task ASavedTextIsInsideItsBoxInItsColourAndOtherPagesStayBlank()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.AddText(1, "Hello\nWorld", "Helvetica", 14, "#C00000", new RectPt(20, 10, 40, 32));
        var path = dir.File("out.pdf");
        await d.SaveAsync(path);

        Assert.Null(await InkAsync(path, 0));
        var ink = (await InkAsync(path, 1))!.Value;
        Assert.True(ink.X0 >= 18 && ink.X1 <= 64 && ink.Y0 >= 8 && ink.Y1 <= 44, ink.ToString());
        Assert.True(ink.X1 - ink.X0 > 20 && ink.Y1 - ink.Y0 > 20, ink.ToString()); // two lines
        Assert.True(ink.Red > 40, ink.ToString());
    }

    [Fact]
    public async Task TextTurnsWithAPageRotatedAfterPlacingAndExtractWritesItToo()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.AddText(0, "Hello", "Times", 14, "#000000", new RectPt(20, 10, 30, 16));
        d.Rotate([0], 90); // page 100 x 200; the text area becomes (74, 20, 16, 30)
        var path = dir.File("extract.pdf");
        await PdfSaver.ExtractAsync(d.Pages, path);
        var ink = (await InkAsync(path, 0))!.Value;
        Assert.True(ink.X0 >= 72 && ink.X1 <= 92 && ink.Y0 >= 18 && ink.Y1 <= 52, ink.ToString());
        Assert.True(ink.Y1 - ink.Y0 > ink.X1 - ink.X0, ink.ToString()); // lying on its side
    }
}
