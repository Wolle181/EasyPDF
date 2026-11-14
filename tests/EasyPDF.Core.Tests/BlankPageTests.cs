using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

public class BlankPageTests
{
    private static async Task<EditableDocument> OpenAsync(TempDir dir, int pages = 3)
    {
        var source = await PdfSource.OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", pages, 0));
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    [Fact]
    public async Task ABlankPageIsInsertedSavedAndItsHelperFileRemoved()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        var index = await d.InsertBlankAsync(1, new PageSizePt(150, 120));
        Assert.Equal(1, index);
        Assert.Equal(4, d.PageCount);
        Assert.True(d.IsDirty);
        var helper = d.Pages[1].Source.Path;
        Assert.True(File.Exists(helper));

        var path = dir.File("out.pdf");
        await d.SaveAsync(path);
        var sizes = await TestPdf.SizesAsync(path);
        Assert.Equal([(100, 200), (150, 120), (110, 200), (120, 200)], sizes);
        Assert.False(File.Exists(helper)); // the save reloads the document; the helper is no longer needed
    }

    [Fact]
    public async Task InsertingABlankPageCanBeUndone()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        await d.InsertBlankAsync(3, new PageSizePt(100, 100)); // at the end
        Assert.Equal(4, d.PageCount);
        d.Undo();
        Assert.Equal(3, d.PageCount);
        d.Redo();
        Assert.Equal(4, d.PageCount);
    }
}
