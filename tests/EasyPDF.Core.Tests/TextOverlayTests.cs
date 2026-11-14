using System;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

public class TextOverlayTests
{
    private static async Task<EditableDocument> OpenAsync(TempDir dir)
    {
        var source = await PdfSource.OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 2, 0));
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    [Fact]
    public async Task AddingChangingAndRemovingATextAreSingleUndoSteps()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        Assert.False(d.IsDirty);
        var text = d.AddText(1, "Hello", "Helvetica", 12, "#FF0000", new RectPt(5, 6, 30, 14));
        Assert.True(d.IsDirty);
        Assert.Equal("Hello", Assert.Single(d.Pages[1].Texts).Text);
        Assert.Empty(d.Pages[0].Texts);

        Assert.True(d.UpdateText(text.Id, "Hello world", new RectPt(5, 6, 60, 14)));
        Assert.Equal("Hello world", d.Pages[1].Texts[0].Text);
        Assert.Equal(60, d.Pages[1].Texts[0].Rect.Width);
        d.Undo();
        Assert.Equal("Hello", d.Pages[1].Texts[0].Text);

        d.Redo();
        Assert.True(d.RemoveText(text.Id));
        Assert.Empty(d.Pages[1].Texts);
        d.Undo();
        Assert.Single(d.Pages[1].Texts);
        d.Undo();
        d.Undo();
        Assert.False(d.IsDirty);
        Assert.False(d.UpdateText(9999, "x", default));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.AddText(5, "x", "Helvetica", 12, "#000000", default));
    }

    [Fact]
    public async Task TextKeepsTheStoredRotation()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        var t = d.AddText(0, "A", "Courier", 10, "#000000", new RectPt(1, 1, 5, 12), 450);
        Assert.Equal(90, t.Rotation);
    }
}
