using System.IO;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;
using static EasyPDF.Core.Tests.RedactionTests;

namespace EasyPDF.Core.Tests;

/// <summary>"Save as image": the saved pages are only pictures of the pages as they look; no text is left in the file.</summary>
public class RasterizeTests
{
    private static async Task<PageSizePt> SizeAsync(string path)
    {
        await using var s = await PdfSource.OpenAsync(path);
        await s.LoadPageSizesAsync();
        return s.GetPageSizeOrEstimate(0);
    }

    [Fact]
    public async Task TheSavedPagesLookTheSameButHoldNoTextAnyMore()
    {
        using var dir = new TempDir();
        var original = dir.File("text.pdf");
        TextPdf(original);
        var keep = await BoxOfAsync(original, "Keep");
        await using var d = await OpenAsync(original);
        d.AddText(0, "ADDEDNOTE", "Helvetica", 14, "#000000", new RectPt(150, 20, 120, 20));
        var path = dir.File("image.pdf");
        await d.SaveAsync(path, rasterize: new PageRasterization(150, false));

        Assert.Empty(await WordsAsync(path));
        Assert.False(FileContains(path, "Keep this line"));
        Assert.False(FileContains(path, "TOPSECRET"));
        Assert.False(FileContains(path, "ADDEDNOTE"));
        Assert.False(FileContains(path, "/Font"));
        Assert.True(FileContains(path, "/Width 625"));             // 300 pt at 150 dpi
        Assert.Equal(new PageSizePt(300, 200), await SizeAsync(path));
        var page = await RenderAsync(path);
        Assert.True(Dark(page, keep) > 20);                         // the text is still there to read, as pixels
        Assert.True(Dark(page, new RectPt(150, 20, 100, 20)) > 20); // and the added text too
        Assert.Equal(path, d.FilePath);                             // the window shows the saved file
    }

    [Fact]
    public async Task ARotatedPageKeepsTheShapeItIsShownIn()
    {
        using var dir = new TempDir();
        var original = dir.File("rotated.pdf");
        TextPdf(original, rotate: 90);
        await using var d = await OpenAsync(original);
        d.Rotate([0], 90);                                          // 90 + 90: upside down, landscape
        var path = dir.File("image.pdf");
        await d.SaveAsync(path, rasterize: PageRasterization.Default);
        Assert.Equal(new PageSizePt(300, 200), await SizeAsync(path));
        Assert.False(FileContains(path, "/Rotate 90"));
        Assert.Empty(await WordsAsync(path));
    }

    [Fact]
    public async Task GreyPagesAreOneChannelPictures()
    {
        using var dir = new TempDir();
        var original = dir.File("text.pdf");
        TextPdf(original);
        await using var d = await OpenAsync(original);
        var grey = dir.File("grey.pdf");
        await d.SaveAsync(grey, rasterize: new PageRasterization(200, true));
        Assert.True(FileContains(grey, "/DeviceGray"));
        Assert.False(FileContains(grey, "/DeviceRGB"));

        await using var e = await OpenAsync(original);
        var colour = dir.File("colour.pdf");
        await e.SaveAsync(colour, rasterize: new PageRasterization(200, false));
        Assert.True(FileContains(colour, "/DeviceRGB"));
        Assert.True(new FileInfo(grey).Length < new FileInfo(colour).Length);
    }
}
