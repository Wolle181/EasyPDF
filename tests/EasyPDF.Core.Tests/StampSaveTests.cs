using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

public class StampSaveTests
{
    /// <summary>BGRA picture; <paramref name="colorAt"/> gets the pixel position and returns (r, g, b, a).</summary>
    private static byte[] Picture(int w, int h, System.Func<int, int, (byte R, byte G, byte B, byte A)> colorAt)
    {
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b, a) = colorAt(x, y);
                var i = (y * w + x) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = a;
            }
        return pixels;
    }

    private static byte[] Red(int w, int h) => Picture(w, h, (_, _) => (255, 0, 0, 255));

    private static async Task<EditableDocument> OpenAsync(TempDir dir, int rotate = 0)
    {
        var path = System.IO.Path.Combine(dir.Path, "a.pdf");
        TestPdf.Create(path, [(200, 100, rotate), (200, 100, rotate)]);
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    /// <summary>The colour (r, g, b) of a point of a saved page rendered at 1 px per point.</summary>
    private static async Task<(int R, int G, int B)[]> SampleAsync(string path, int page, params (int X, int Y)[] points)
    {
        await using var s = await PdfSource.OpenAsync(path);
        await s.LoadPageSizesAsync();
        var size = s.GetPageSizeOrEstimate(page);
        var img = await s.RenderAsync(page, (int)size.Width, (int)size.Height);
        return [.. points.Select(p => { var i = p.Y * img.Stride + p.X * 4; return ((int)img.Pixels[i + 2], (int)img.Pixels[i + 1], (int)img.Pixels[i]); })];
    }

    private static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 60 && c.B < 60;
    private static bool IsBlue((int R, int G, int B) c) => c.B > 200 && c.G < 60 && c.R < 60;
    private static bool IsWhite((int R, int G, int B) c) => c.R > 240 && c.G > 240 && c.B > 240;

    [Fact]
    public async Task ASavedStampIsOnThePageAtItsPlace()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.AddStamp(1, [1], 8, 4, new RectPt(20, 10, 40, 20), 0, Red(8, 4));
        var path = dir.File("out.pdf");
        await d.SaveAsync(path);

        var onStamp = await SampleAsync(path, 1, (40, 20), (22, 12), (58, 28));
        Assert.All(onStamp, c => Assert.True(IsRed(c), c.ToString()));
        var off = await SampleAsync(path, 1, (10, 5), (70, 40), (150, 80), (40, 35));
        Assert.All(off, c => Assert.True(IsWhite(c), c.ToString()));
        Assert.True(IsWhite((await SampleAsync(path, 0, (40, 20)))[0])); // the other page is untouched
    }

    [Fact]
    public async Task TransparentPixelsOfAStampStayTransparent()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        // left half red, right half fully transparent
        d.AddStamp(0, [1], 8, 4, new RectPt(20, 10, 40, 20), 0, Picture(8, 4, (x, _) => x < 4 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)0)));
        var path = dir.File("out.pdf");
        await d.SaveAsync(path);
        var c = await SampleAsync(path, 0, (30, 20), (50, 20));
        Assert.True(IsRed(c[0]), c[0].ToString());
        Assert.True(IsWhite(c[1]), c[1].ToString());
    }

    [Fact]
    public async Task AStampOnAPageWithItsOwnRotateLandsWhereItWasShown()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir, rotate: 90); // shown as 100 x 200
        d.AddStamp(0, [1], 8, 4, new RectPt(10, 20, 40, 20), 0, Red(8, 4));
        var path = dir.File("out.pdf");
        await d.SaveAsync(path);
        var c = await SampleAsync(path, 0, (30, 30), (80, 150), (70, 30));
        Assert.True(IsRed(c[0]), c[0].ToString());
        Assert.True(IsWhite(c[1]), c[1].ToString());
        Assert.True(IsWhite(c[2]), c[2].ToString());
    }

    [Fact]
    public async Task RotatingThePageAfterPlacingTurnsTheStampWithIt()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.AddStamp(0, [1], 8, 4, new RectPt(20, 10, 40, 20), 0, Red(8, 4));
        d.Rotate([0], 90); // page 200 x 100 becomes 100 x 200; the stamp area becomes (70, 20, 20, 40)
        var path = dir.File("out.pdf");
        await d.SaveAsync(path);
        var c = await SampleAsync(path, 0, (80, 40), (30, 30), (80, 150));
        Assert.True(IsRed(c[0]), c[0].ToString());
        Assert.True(IsWhite(c[1]), c[1].ToString());
        Assert.True(IsWhite(c[2]), c[2].ToString());
    }

    [Fact]
    public async Task AStampKeepsItsOwnRotationAndExtractWritesItToo()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        // 8 x 4 picture, left half red, right half blue, turned clockwise: red ends up on top
        var picture = Picture(8, 4, (x, _) => x < 4 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255));
        d.AddStamp(0, [1], 8, 4, new RectPt(20, 10, 20, 40), 90, picture);
        var path = dir.File("extract.pdf");
        await PdfSaver.ExtractAsync(d.Pages, path);
        var c = await SampleAsync(path, 0, (30, 20), (30, 40), (45, 30));
        Assert.True(IsRed(c[0]), c[0].ToString());
        Assert.True(IsBlue(c[1]), c[1].ToString());
        Assert.True(IsWhite(c[2]), c[2].ToString());
    }
}
