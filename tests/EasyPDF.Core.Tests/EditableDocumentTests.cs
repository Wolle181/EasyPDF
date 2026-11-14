using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

public class EditableDocumentTests
{
    /// <summary>Opens a numbered document; the page width (100 + 10·i) identifies the original page.</summary>
    private static async Task<EditableDocument> OpenAsync(TempDir dir, string name = "a.pdf", int pages = 5, int widthBase = 0)
    {
        var source = await PdfSource.OpenAsync(TestPdf.Numbered(dir.Path, name, pages, widthBase));
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    private static int[] Order(EditableDocument d) => [.. d.Pages.Select(p => p.SourceIndex)];

    [Fact]
    public async Task MoveSingleAndMultiplePages()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        Assert.Equal(1, d.Move([3], 1));
        Assert.Equal(new[] { 0, 3, 1, 2, 4 }, Order(d));

        d.Undo();
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Order(d));

        Assert.Equal(3, d.Move([1, 0], 5)); // selection order does not matter, relative order is kept
        Assert.Equal(new[] { 2, 3, 4, 0, 1 }, Order(d));
    }

    [Fact]
    public async Task MoveToOwnPositionIsNoChange()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.Move([2], 2);
        d.Move([2], 3);
        Assert.False(d.CanUndo);
        Assert.False(d.IsDirty);
    }

    [Fact]
    public async Task RotationAddsUpAndIsOneUndoStep()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.Rotate([0, 1], 270);
        Assert.Equal(new[] { 270, 270, 0, 0, 0 }, d.Pages.Select(p => p.ExtraRotation).ToArray());
        d.Rotate([0], 180);
        Assert.Equal(90, d.Pages[0].ExtraRotation);
        d.Rotate([1], -90);
        Assert.Equal(180, d.Pages[1].ExtraRotation);

        d.Undo(); d.Undo();
        Assert.Equal(270, d.Pages[0].ExtraRotation);
        d.Undo();
        Assert.All(d.Pages, p => Assert.Equal(0, p.ExtraRotation));
    }

    [Fact]
    public async Task DisplaySizeSwapsForQuarterTurns()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        var before = d.Pages[0].DisplaySize;
        d.Rotate([0], 90);
        Assert.Equal(before.Height, d.Pages[0].DisplaySize.Width);
        d.Rotate([0], 90);
        Assert.Equal(before.Width, d.Pages[0].DisplaySize.Width);
    }

    [Fact]
    public async Task DeleteAndUndoRedo()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.Delete([1, 3]);
        Assert.Equal(new[] { 0, 2, 4 }, Order(d));
        Assert.True(d.IsDirty);
        d.Undo();
        Assert.Equal(5, d.PageCount);
        Assert.False(d.IsDirty); // back at the saved state
        d.Redo();
        Assert.Equal(3, d.PageCount);
        Assert.True(d.CanUndo);
        Assert.False(d.CanRedo);
    }

    [Fact]
    public async Task CannotDeleteAllPages()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir, pages: 2);
        Assert.Throws<System.InvalidOperationException>(() => d.Delete([0, 1]));
        Assert.Equal(2, d.PageCount);
    }

    [Fact]
    public async Task NewEditClearsRedo()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.Delete([0]);
        d.Undo();
        Assert.True(d.CanRedo);
        d.Rotate([0], 90);
        Assert.False(d.CanRedo);
    }

    [Fact]
    public async Task AfterSavingLandscapePagesKeepTheirSize()
    {
        // The reopened file knows the size of every page, so landscape pages after a portrait first page are laid out
        // (and rendered) in landscape, not distorted
        using var dir = new TempDir();
        var path = dir.File("mixed.pdf");
        TestPdf.Create(path, [(595, 842, 0), (842, 595, 0), (595, 842, 90)]);
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        await using var mixed = EditableDocument.FromSource(source);
        (int, int)[] expected = [(595, 842), (842, 595), (842, 595)];
        Assert.Equal(expected, mixed.Pages.Select(p => ((int)p.DisplaySize.Width, (int)p.DisplaySize.Height)));

        mixed.Move([2], 1);   // a change, then save over the file (it is reopened); the first page stays portrait
        await mixed.SaveAsync(path);
        Assert.Equal(expected, mixed.Pages.Select(p => ((int)p.DisplaySize.Width, (int)p.DisplaySize.Height)));
    }

    [Fact]
    public async Task InsertFromIsOneUndoStep()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir, pages: 3);
        var s1 = await PdfSource.OpenAsync(TestPdf.Numbered(dir.Path, "b.pdf", 2, 500));
        var s2 = await PdfSource.OpenAsync(TestPdf.Numbered(dir.Path, "c.pdf", 1, 900));

        Assert.Equal(1, d.InsertFrom([s1, s2], 1));
        Assert.Equal(6, d.PageCount);
        Assert.Equal([d.Pages[0].Source, s1, s1, s2, d.Pages[0].Source, d.Pages[0].Source], d.Pages.Select(p => p.Source).ToArray());
        Assert.Equal(3, d.Sources.Count);

        d.Undo();
        Assert.Equal(3, d.PageCount);
        d.Redo();
        Assert.Equal(6, d.PageCount);
        Assert.Equal(new[] { 0, 0, 1, 0, 1, 2 }, Order(d));
    }

    [Fact]
    public async Task InsertingAPageSubsetKeepsOrderAndIsOneUndoStep()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir, pages: 3);
        var s = await PdfSource.OpenAsync(TestPdf.Numbered(dir.Path, "b.pdf", 5, 500));

        Assert.Equal(1, d.InsertFrom(s, [3, 1], 1));
        Assert.Equal(5, d.PageCount);
        Assert.Equal(new[] { 0, 3, 1, 1, 2 }, Order(d));
        Assert.Same(s, d.Pages[1].Source);

        d.Undo();
        Assert.Equal(3, d.PageCount);
        Assert.Throws<System.ArgumentOutOfRangeException>(() => d.InsertFrom(s, [5], 0));
    }

    [Fact]
    public async Task ChangedEventFires()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        var kinds = new System.Collections.Generic.List<DocumentChangeKind>();
        d.Changed += kinds.Add;
        d.Delete([0]);
        d.Undo();
        d.Redo();
        Assert.Equal(3, kinds.Count);
        Assert.All(kinds, k => Assert.Equal(DocumentChangeKind.Edited, k));
    }

    [Fact]
    public async Task ReplacingAStampPictureIsOneUndoStepAndKeepsThePlace()
    {
        using var dir = new TempDir();
        var doc = await OpenAsync(dir);
        var placed = doc.AddStamp(1, [1], 10, 10, new RectPt(5, 6, 7, 8), 90, new byte[400]);

        var changed = doc.ReplaceStampPicture(placed.Id, [2], 20, 20, new byte[1600], null);

        Assert.NotNull(changed);
        Assert.NotEqual(placed.Id, changed.Id);
        Assert.Equal(placed.Rect, changed.Rect);
        Assert.Equal(90, changed.Rotation);
        Assert.Equal(new byte[] { 2 }, Assert.Single(doc.Pages[1].Stamps).Image);
        doc.Undo();
        Assert.Same(placed, Assert.Single(doc.Pages[1].Stamps));
        Assert.Null(doc.ReplaceStampPicture(12345, [2], 20, 20, new byte[1600], null));
    }
}
