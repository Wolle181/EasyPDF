using System;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;
using Xunit;
using static EasyPDF.Core.Tests.RedactionTests;

namespace EasyPDF.Core.Tests;

/// <summary>
/// Pasted areas: a part of a page copied as a picture and placed somewhere else, opaque. Until saving it is a movable
/// stamp; saving removes what lies under it from the file (like a white-out box) and puts the picture on top.
/// </summary>
public class PastedAreaTests
{
    private const double Scale = 300 / 72.0;

    /// <summary>The area of the page as seen (300 dpi), as straight BGRA without row padding.</summary>
    private static async Task<(byte[] Bgra, int Width, int Height)> CopyAsync(EditableDocument d, int pageIndex, RectPt area)
    {
        var page = d.Pages[pageIndex];
        var size = page.DisplaySize;
        int x = (int)Math.Floor(area.X * Scale), y = (int)Math.Floor(area.Y * Scale);
        int w = (int)Math.Ceiling(area.Width * Scale), h = (int)Math.Ceiling(area.Height * Scale);
        var rendered = await page.Source.RenderAreaAsync(page.SourceIndex, (int)Math.Round(size.Width * Scale), (int)Math.Round(size.Height * Scale),
            page.ExtraRotation, x, y, w, h);
        var bgra = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Array.Copy(rendered.Pixels, row * rendered.Stride, bgra, row * w * 4, w * 4);
        return (bgra, w, h);
    }

    [Fact]
    public async Task RenderingAnAreaGivesThePixelsOfTheWholePageRender()
    {
        using var dir = new TempDir();
        var path = dir.File("text.pdf");
        TextPdf(path);
        await using var s = await PdfSource.OpenAsync(path);
        var whole = await s.RenderAsync(0, 600, 400);
        var part = await s.RenderAreaAsync(0, 600, 400, 0, 30, 50, 120, 80);
        Assert.Equal(120, part.Width);
        Assert.Equal(80, part.Height);
        var dark = 0;
        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 120; x++)
                for (var c = 0; c < 4; c++)
                {
                    var expected = whole.Pixels[(y + 50) * whole.Stride + (x + 30) * 4 + c];
                    Assert.Equal(expected, part.Pixels[y * part.Stride + x * 4 + c]);
                    if (c == 0 && expected < 128)
                        dark++;
                }
        Assert.True(dark > 0, "The compared area should contain text");
    }

    [Fact]
    public async Task PastingIsOneUndoStepAndHidesTheWordsUnderIt()
    {
        using var dir = new TempDir();
        var path = dir.File("text.pdf");
        TextPdf(path);
        var secret = await BoxOfAsync(path, "TOPSECRET");
        await using var d = await OpenAsync(path);
        var (bgra, w, h) = await CopyAsync(d, 0, new RectPt(15, 25, 120, 22));
        var stamp = d.AddStamp(0, [], w, h, new RectPt(secret.X, secret.Y, 120, 22), bgra: bgra, coversContent: true);

        Assert.True(stamp.CoversContent);
        Assert.True(d.RemovesContent);
        Assert.Equal(0, d.RedactedPageCount); // the save warning is about blacked-out content only
        Assert.Contains(d.Pages[0].RemovalAreas, a => a.Id == stamp.Id && a.Rect == stamp.Rect && a.Fill == RedactionFill.White);
        var (text, _) = await PageText.ReadPagesAsync(d.Pages);
        Assert.DoesNotContain("TOPSECRET", text);
        Assert.Contains("Keep this line", text);

        d.Undo();
        Assert.False(d.RemovesContent);
        Assert.Empty(d.Pages[0].Stamps);
        Assert.False(d.IsDirty);
    }

    [Fact]
    public async Task SavingRemovesWhatLiesUnderThePastedAreaAndShowsThePicture()
    {
        using var dir = new TempDir();
        var path = dir.File("text.pdf");
        TextPdf(path);
        var keep = await BoxOfAsync(path, "Keep");
        var secret = await BoxOfAsync(path, "TOPSECRET");
        var before = await RenderAsync(path);
        var copied = new RectPt(15, 25, 120, 22);
        var target = new RectPt(15, secret.Y - 4, 120, 22);

        var saved = dir.File("saved.pdf");
        await using (var d = await OpenAsync(path))
        {
            var (bgra, w, h) = await CopyAsync(d, 0, copied);
            d.AddStamp(0, [], w, h, target, bgra: bgra, coversContent: true);
            await d.SaveAsync(saved);
        }

        Assert.False(FileContains(saved, "TOPSECRET"), "The pasted-over text is still in the file");
        Assert.False(FileContains(saved, "value"));
        // Only the original line is real text; the pasted copy is a picture
        var words = (await WordsAsync(saved)).Select(x => x.Text).ToList();
        Assert.Equal(["Keep", "this", "line"], words);

        var after = await RenderAsync(saved);
        var darkOriginal = Dark(before, copied);
        Assert.InRange(Dark(after, target), darkOriginal * 0.7, darkOriginal * 1.3); // the copy shows the copied line
        Assert.InRange(Dark(after, copied), darkOriginal * 0.7, darkOriginal * 1.3); // the original stays
        Assert.True(Dark(before, keep) > 0);
    }

    [Fact]
    public async Task ABoxOverAPastedAreaIsPaintedIntoItsPictureAlsoWhenTurned()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "a.pdf", 1);
        // A grey picture of 100 × 50 px, turned by 90° onto a 50 × 100 pt area; a black box over the upper 30 pt of it
        var bgra = new byte[100 * 50 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = ((byte)128, (byte)128, (byte)128, (byte)255);
        var area = new RectPt(20, 20, 50, 100);
        var box = new RectPt(20, 20, 50, 30);
        var saved = dir.File("saved.pdf");
        await using (var d = await OpenAsync(path))
        {
            d.AddStamp(0, [], 100, 50, area, rotation: 90, bgra: bgra, coversContent: true);
            d.AddRedactions([(0, box)], RedactionFill.Black);
            await d.SaveAsync(saved);
        }

        var after = await RenderAsync(saved);
        Assert.Equal(0, Range(after, box).Max);
        var (min, max) = Range(after, new RectPt(20, 52, 50, 66));
        Assert.InRange(min, 120, 136);
        Assert.InRange(max, 120, 136);
    }
}
