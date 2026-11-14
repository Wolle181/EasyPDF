using System;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

public class StampOverlayTests
{
    private static readonly PageSizePt Page = new(200, 100); // landscape: rotated views are 100 x 200

    [Theory]
    [InlineData(0, 10, 20, 30, 40)]
    [InlineData(90, 40, 10, 40, 30)]    // (H - y - h, x, h, w) = (100-20-40, 10, 40, 30)
    [InlineData(180, 160, 40, 30, 40)]  // (W - x - w, H - y - h)
    [InlineData(270, 20, 160, 40, 30)]  // (y, W - x - w, h, w)
    public void RotatingARectFollowsThePage(int degrees, double x, double y, double w, double h)
    {
        var r = PageGeometry.Rotate(new RectPt(10, 20, 30, 40), Page, degrees);
        Assert.Equal(new RectPt(x, y, w, h), r);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(-90)]
    public void UnrotateIsTheInverse(int degrees)
    {
        var original = new RectPt(12, 7, 33, 21);
        var view = PageGeometry.Rotate(original, Page, degrees);
        var back = PageGeometry.Unrotate(view, Page, degrees);
        Assert.Equal(original, back);
    }

    [Fact]
    public void InitialSizeUsesNinetySixDpiAndFitsHalfThePage()
    {
        // 100 x 40 px = 75 x 30 pt, fits into half of 200 x 300
        Assert.Equal((75.0, 30.0), PageGeometry.InitialStampSize(100, 40, new PageSizePt(200, 300)));
        // 1000 x 500 px = 750 x 375 pt, reduced to half the page width (100) keeping the ratio
        var (w, h) = PageGeometry.InitialStampSize(1000, 500, new PageSizePt(200, 300));
        Assert.Equal(100, w, 6);
        Assert.Equal(50, h, 6);
        // With the resolution stored in the picture: 236 px at 300 dpi = 20 mm = 56.64 pt
        Assert.Equal((56.64, 56.64), PageGeometry.InitialStampSize(236, 236, new PageSizePt(595, 842), 300));
    }

    [Fact]
    public void CenteredStampIsKeptInsideThePage()
    {
        var page = new PageSizePt(200, 100);
        Assert.Equal(new RectPt(80, 40, 40, 20), PageGeometry.CenteredInside(100, 50, 40, 20, page));
        Assert.Equal(new RectPt(0, 0, 40, 20), PageGeometry.CenteredInside(-50, -50, 40, 20, page));
        Assert.Equal(new RectPt(160, 80, 40, 20), PageGeometry.CenteredInside(500, 500, 40, 20, page));
    }

    private static async Task<EditableDocument> OpenAsync(TempDir dir, int pages = 3)
    {
        var source = await PdfSource.OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", pages, 0));
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    [Fact]
    public async Task AddingAStampIsOneUndoStepAndMakesTheDocumentDirty()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        Assert.False(d.IsDirty);
        var stamp = d.AddStamp(1, [1, 2, 3], 10, 5, new RectPt(5, 6, 20, 10));
        Assert.True(d.IsDirty);
        Assert.True(d.HasStamps);
        Assert.Equal([stamp], d.Pages[1].Stamps);
        Assert.Empty(d.Pages[0].Stamps);

        d.Undo();
        Assert.False(d.HasStamps);
        Assert.False(d.IsDirty);
        d.Redo();
        Assert.Equal([stamp], d.Pages[1].Stamps);
    }

    [Fact]
    public async Task StampsTravelWithTheirPageWhenMovedOrRotated()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.AddStamp(0, [1], 1, 1, new RectPt(1, 1, 5, 5));
        d.Move([0], 3);
        Assert.Single(d.Pages[2].Stamps);
        d.Rotate([2], 90);
        Assert.Single(d.Pages[2].Stamps); // stays in page space, only the view turns
        Assert.Equal(new RectPt(1, 1, 5, 5), d.Pages[2].Stamps[0].Rect);
    }

    [Fact]
    public async Task SavingRefusesPlacedStampsInsteadOfDroppingThem()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.AddStamp(0, [1], 1, 1, new RectPt(1, 1, 5, 5));
        await Assert.ThrowsAsync<NotSupportedException>(() => d.SaveAsync(dir.File("out.pdf")));
        Assert.True(d.IsDirty); // nothing was changed by the failed save
        Assert.Throws<ArgumentOutOfRangeException>(() => d.AddStamp(9, [1], 1, 1, default));
    }
}
