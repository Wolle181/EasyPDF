using System;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

/// <summary>Replacing consecutive pages by pages of another document (a file or the copy of another window's document).</summary>
public class ReplacePagesTests
{
    private static async Task<EditableDocument> OpenAsync(string path)
    {
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    private static int[] Widths(EditableDocument d) => [.. d.Pages.Select(p => (int)p.DisplaySize.Width)];

    [Fact]
    public async Task ReplacesConsecutivePagesInOneUndoStep()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 5));          // 100 110 120 130 140
        var other = await OpenAsync(TestPdf.Numbered(dir.Path, "b.pdf", 4, widthBase: 500));  // 600 610 620 630

        Assert.Equal(1, d.Replace(1, 3, other, [3, 0]));
        Assert.Equal([100, 630, 600, 140], Widths(d));
        Assert.Contains(other.Pages[0].Source, d.Sources);
        Assert.Equal(d.PageCount, d.Pages.Select(p => p.Id).Distinct().Count());

        d.Undo();
        Assert.Equal([100, 110, 120, 130, 140], Widths(d));
        d.Redo();
        Assert.Equal([100, 630, 600, 140], Widths(d));
    }

    [Fact]
    public async Task AllPagesCanBeReplacedAndAPageTakenTwiceGetsTwoIds()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 2));
        var other = await OpenAsync(TestPdf.Numbered(dir.Path, "b.pdf", 1, widthBase: 500));

        d.Replace(0, 2, other, [0, 0]);
        Assert.Equal([600, 600], Widths(d));
        Assert.NotEqual(d.Pages[0].Id, d.Pages[1].Id);
    }

    [Fact]
    public async Task TheCopyOfAnOpenDocumentBringsItsChangesAndTheResultSaves()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 3));          // 100 110 120
        var window = await OpenAsync(TestPdf.Numbered(dir.Path, "b.pdf", 2, widthBase: 500));
        window.Rotate([1], 90);                                                              // unsaved change in the other window

        d.Replace(2, 1, await window.CopyAsync(), [1, 0]);
        await window.DisposeAsync();                                                         // the other window is closed
        Assert.Equal([90, 0], d.Pages.Skip(2).Select(p => p.ExtraRotation));

        await d.SaveAsync(dir.File("out.pdf"));
        Assert.Equal([(100, 200), (110, 200), (200, 610), (600, 200)], await TestPdf.SizesAsync(dir.File("out.pdf")));
    }

    [Fact]
    public async Task InvalidArgumentsAreRefused()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 3));
        await using var other = await OpenAsync(TestPdf.Numbered(dir.Path, "b.pdf", 2, widthBase: 500));

        Assert.Throws<ArgumentOutOfRangeException>(() => d.Replace(2, 2, other, [0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Replace(0, 0, other, [0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Replace(0, 1, other, [2]));
        Assert.Throws<ArgumentException>(() => d.Replace(0, 1, other, []));
        Assert.Throws<ArgumentException>(() => d.Replace(0, 1, d, [0]));
        Assert.False(d.CanUndo);
    }
}
