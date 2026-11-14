using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

/// <summary>Copies of an open document (used to concatenate the document of another window).</summary>
public class DocumentCopyTests
{
    private static async Task<EditableDocument> OpenAsync(string path)
    {
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    [Fact]
    public async Task TheCopyHasTheEditedPagesAndOutlivesTheOriginal()
    {
        using var dir = new TempDir();
        var original = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 4));   // 100 110 120 130
        original.Delete([0]);
        original.Move([2], 0);             // 130 110 120
        original.Rotate([1], 90);

        var copy = await original.CopyAsync();
        Assert.False(copy.HasFile);
        Assert.True(copy.IsDirty);
        Assert.False(copy.CanUndo);
        Assert.Equal(original.Pages.Select(p => (p.SourceIndex, p.ExtraRotation)), copy.Pages.Select(p => (p.SourceIndex, p.ExtraRotation)));
        Assert.Empty(copy.Pages.Select(p => p.Id).Intersect(original.Pages.Select(p => p.Id)));
        Assert.DoesNotContain(copy.Sources, s => original.Sources.Contains(s));

        original.Undo();                   // later edits of the original do not reach the copy
        await original.DisposeAsync();
        await copy.SaveAsync(dir.File("copy.pdf"));
        Assert.Equal([(130, 200), (200, 110), (120, 200)], await TestPdf.SizesAsync(dir.File("copy.pdf")));
        await copy.DisposeAsync();
    }

    [Fact]
    public async Task ABlankPageFileIsRemovedOnlyAfterBothDocumentsAreClosed()
    {
        using var dir = new TempDir();
        var original = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 1));
        await original.InsertBlankAsync(1, new PageSizePt(150, 120));
        var helper = original.Pages[1].Source.Path;

        var copy = await original.CopyAsync();
        await original.DisposeAsync();
        Assert.Equal(2, copy.PageCount);
        await copy.SaveAsync(dir.File("copy.pdf"));
        Assert.Equal([(100, 200), (150, 120)], await TestPdf.SizesAsync(dir.File("copy.pdf")));
        await copy.DisposeAsync();
        Assert.False(File.Exists(helper));
    }

    [Fact]
    public async Task CopyingFailsCleanlyWhenAFileIsGone()
    {
        using var dir = new TempDir();
        var a = TestPdf.Numbered(dir.Path, "a.pdf", 1);
        var b = TestPdf.Numbered(dir.Path, "b.pdf", 1, widthBase: 500);
        await using var original = await OpenAsync(a);
        var source = await PdfSource.OpenAsync(b);
        original.InsertFrom([source], 1);
        await source.CloseAsync();         // lets the file be deleted (as if another program removed it)
        File.Delete(b);

        await Assert.ThrowsAsync<PdfOpenException>(() => original.CopyAsync());
    }
}
