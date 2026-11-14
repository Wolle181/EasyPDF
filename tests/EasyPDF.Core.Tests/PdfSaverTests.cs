using System.IO;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

public class PdfSaverTests
{
    private static async Task<EditableDocument> OpenAsync(string path)
    {
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    // Page i of a numbered file has width 100 + 10·i + widthBase

    [Fact]
    public async Task SaveAsRoundTripKeepsOrderAndRotation()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 5));
        d.Move([4], 0);   // 4 0 1 2 3
        d.Delete([2]);    // 4 0 2 3   (page 1 removed)
        d.Rotate([1], 90);
        var target = dir.File("out.pdf");
        await d.SaveAsync(target);

        var sizes = await TestPdf.SizesAsync(target);
        // pages 4,0,2,3 -> widths 140,100,120,130; the rotated page 0 is 200 wide and 100 high
        Assert.Equal(new[] { (140, 200), (200, 100), (120, 200), (130, 200) }, sizes);
        Assert.False(d.IsDirty);
        Assert.False(d.CanUndo);
        Assert.Equal(target, d.FilePath);
        Assert.Equal(4, d.PageCount);
    }

    [Fact]
    public async Task RotationAddsToOriginalPageRotation()
    {
        using var dir = new TempDir();
        var path = dir.File("a.pdf");
        TestPdf.Create(path, [(100, 200, 270), (100, 200, 0)]); // first page is already turned (displayed 200x100)
        await using var d = await OpenAsync(path);
        d.Rotate([0], 180);   // 270 + 180 = 90 -> displayed 200x100
        d.Rotate([1], 180);   // stays 100x200
        d.Rotate([1], 90);    // -> 200x100
        await d.SaveAsync(dir.File("out.pdf"));
        Assert.Equal(new[] { (200, 100), (200, 100) }, await TestPdf.SizesAsync(dir.File("out.pdf")));
    }

    [Fact]
    public async Task OverwritingTheOpenFileWorks()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "a.pdf", 4);
        await using var d = await OpenAsync(path);
        d.Delete([0]);
        await d.SaveAsync(path);

        Assert.Equal(new[] { 110, 120, 130 }, await TestPdf.WidthsAsync(path));
        Assert.Equal(3, d.PageCount);
        Assert.True(d.BaseSource.IsOpen);
        // still usable: edit and save again
        d.Move([0], 3);
        await d.SaveAsync(path);
        Assert.Equal(new[] { 120, 130, 110 }, await TestPdf.WidthsAsync(path));
        Assert.Empty(Directory.GetFiles(dir.Path, ".*.tmp"));
    }

    [Fact]
    public async Task BookmarksFollowReorderingAndLoseDeletedTargets()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "a.pdf", 4, outline: [("P0", 0), ("P1", 1), ("P3", 3)]);
        await using var d = await OpenAsync(path);
        d.Move([3], 0);   // 3 0 1 2
        d.Delete([2]);    // 3 0 2  (page 1 deleted)
        var target = dir.File("out.pdf");
        await d.SaveAsync(target);

        await using var s = await PdfSource.OpenAsync(target);
        var outline = await s.ReadOutlineAsync();
        var byTitle = outline.ToDictionary(o => o.Title, o => o.SourcePageIndex);
        Assert.Equal(0, byTitle["P3"]);
        Assert.Equal(1, byTitle["P0"]);
        if (byTitle.TryGetValue("P1", out var p1))
            Assert.Equal(-1, p1); // may be dropped or lose its target, but must not point to another page
    }

    [Fact]
    public async Task InsertedPagesAreSavedInPlaceAndBookmarksOfBaseKept()
    {
        using var dir = new TempDir();
        var a = TestPdf.Numbered(dir.Path, "a.pdf", 3, outline: [("Last", 2)]);
        var b = TestPdf.Numbered(dir.Path, "b.pdf", 2, widthBase: 500);
        await using var d = await OpenAsync(a);
        d.InsertFrom([await PdfSource.OpenAsync(b)], 1);   // 100 | 600 610 | 110 120
        d.Rotate([1], 90);
        var target = dir.File("out.pdf");
        await d.SaveAsync(target);

        Assert.Equal(new[] { (100, 200), (200, 600), (610, 200), (110, 200), (120, 200) }, await TestPdf.SizesAsync(target));
        await using var s = await PdfSource.OpenAsync(target);
        var outline = await s.ReadOutlineAsync();
        Assert.Equal(4, outline.Single().SourcePageIndex);
        Assert.Equal(5, d.PageCount);
    }

    [Fact]
    public async Task InsertingAPageOfTheSameFileWorks()
    {
        using var dir = new TempDir();
        var a = TestPdf.Numbered(dir.Path, "a.pdf", 2);
        await using var d = await OpenAsync(a);
        d.InsertFrom([await PdfSource.OpenAsync(a)], 2);
        Assert.Equal(4, d.PageCount);
        await d.SaveAsync(dir.File("out.pdf"));
        Assert.Equal(new[] { 100, 110, 100, 110 }, await TestPdf.WidthsAsync(dir.File("out.pdf")));
    }

    [Fact]
    public async Task FailedSaveLeavesDocumentUntouched()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 3));
        d.Delete([0]);
        await Assert.ThrowsAnyAsync<System.Exception>(() => d.SaveAsync(Path.Combine(dir.Path, "no-such-dir", "x.pdf")));
        Assert.True(d.IsDirty);
        Assert.True(d.CanUndo);
        Assert.Equal(2, d.PageCount);
    }

    [Fact]
    public async Task ExtractWritesSelectedPagesInCurrentOrderWithRotation()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 5));
        d.Move([4], 0);              // 4 0 1 2 3
        d.Rotate([2], 90);           // page 1 turned
        var target = dir.File("extract.pdf");
        await PdfSaver.ExtractAsync([d.Pages[2], d.Pages[0]], target);

        Assert.Equal(new[] { (200, 110), (140, 200) }, await TestPdf.SizesAsync(target));
        Assert.Equal(5, d.PageCount);
    }

    [Fact]
    public void SplitPlans()
    {
        Assert.Equal([new SplitPart(0, 3), new SplitPart(3, 3), new SplitPart(6, 1)], SplitPlan.EveryN(7, 3));
        Assert.Equal([new SplitPart(0, 7)], SplitPlan.EveryN(7, 10));
        Assert.Equal([new SplitPart(0, 2), new SplitPart(2, 3), new SplitPart(5, 2)], SplitPlan.AtPages(7, [5, 2, 0, 2, 99
        ]));
        Assert.Equal([new SplitPart(0, 4)], SplitPlan.AtPages(4, new int[0]));
    }

    [Fact]
    public async Task SplitWritesNumberedFiles()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 7));
        var files = await PdfSaver.SplitAsync([.. d.Pages], SplitPlan.EveryN(7, 3), dir.Path, "a");

        Assert.Equal(new[] { "a_part01.pdf", "a_part02.pdf", "a_part03.pdf" }, files.Select(Path.GetFileName).ToArray());
        Assert.Equal(new[] { 100, 110, 120 }, await TestPdf.WidthsAsync(files[0]));
        Assert.Equal(new[] { 130, 140, 150 }, await TestPdf.WidthsAsync(files[1]));
        Assert.Equal(new[] { 160 }, await TestPdf.WidthsAsync(files[2]));
    }

    [Fact]
    public async Task ConcatenateThreeFilesIncludingAnEditedDocument()
    {
        using var dir = new TempDir();
        var a = TestPdf.Numbered(dir.Path, "a.pdf", 3, outline: [("A-last", 2)]);   // 100 110 120
        var b = TestPdf.Numbered(dir.Path, "b.pdf", 2, widthBase: 500);                       // 600 610
        var c = TestPdf.Numbered(dir.Path, "c.pdf", 2, widthBase: 900);                       // 1000 1010

        var edited = await OpenAsync(a);
        edited.Delete([0]);                // 110 120 (unsaved edit)
        var docB = await OpenAsync(b);
        var docC = await OpenAsync(c);
        await using var combined = EditableDocument.Combine([docB, edited, docC]);

        Assert.False(combined.HasFile);
        Assert.True(combined.IsDirty);
        Assert.Equal(6, combined.PageCount);

        var target = dir.File("all.pdf");
        await combined.SaveAsync(target);
        Assert.Equal(new[] { 600, 610, 110, 120, 1000, 1010 }, await TestPdf.WidthsAsync(target));
        Assert.Equal(target, combined.FilePath);
        Assert.False(combined.IsDirty);
    }

    [Fact]
    public async Task ConcatenatedDocumentCanBeEditedBeforeSaving()
    {
        using var dir = new TempDir();
        var docA = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 2));
        var docB = await OpenAsync(TestPdf.Numbered(dir.Path, "b.pdf", 2, widthBase: 500));
        await using var combined = EditableDocument.Combine([docA, docB]);
        combined.Move([3], 0);
        await combined.SaveAsync(dir.File("all.pdf"));
        Assert.Equal(new[] { 610, 100, 110, 600 }, await TestPdf.WidthsAsync(dir.File("all.pdf")));
    }

    [Fact]
    public async Task ConcatenatedDocumentKeepsBookmarksOfFirstFile()
    {
        using var dir = new TempDir();
        var docA = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 2, outline: [("First", 1)]));
        var docB = await OpenAsync(TestPdf.Numbered(dir.Path, "b.pdf", 2, widthBase: 500, outline: [("Other", 0)]));
        await using var combined = EditableDocument.Combine([docA, docB]);
        await combined.SaveAsync(dir.File("all.pdf"));

        await using var s = await PdfSource.OpenAsync(dir.File("all.pdf"));
        var outline = await s.ReadOutlineAsync();
        Assert.Equal("First", outline.Single().Title);
        Assert.Equal(1, outline.Single().SourcePageIndex);
    }
}

public class CancellationTests
{
    private static async Task<EditableDocument> OpenAsync(string path)
    {
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    /// <summary>Cancels as soon as the first progress value arrives.</summary>
    private sealed class CancelOnProgress(CancellationTokenSource cts) : IProgress<double>
    {
        public void Report(double value) => cts.Cancel();
    }

    [Fact]
    public async Task CancelledSplitLeavesNoFilesBehind()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 6));
        using var cts = new CancellationTokenSource();
        var outDir = dir.File("out");
        Directory.CreateDirectory(outDir);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PdfSaver.SplitAsync([.. d.Pages], SplitPlan.EveryN(6, 1), outDir, "p", new CancelOnProgress(cts), cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(outDir));
    }

    [Fact]
    public async Task CancelledExtractWritesNothing()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 3));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PdfSaver.ExtractAsync([.. d.Pages], dir.File("x.pdf"), null, null, cts.Token));
        Assert.False(File.Exists(dir.File("x.pdf")));
        Assert.Empty(Directory.GetFiles(dir.Path, ".*.tmp"));
    }

    [Fact]
    public async Task CancelledSaveKeepsTheOriginalAndTheEdits()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "a.pdf", 4);
        var before = await File.ReadAllBytesAsync(path);
        await using var d = await OpenAsync(path);
        d.Delete([0]);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.SaveAsync(path, new CancelOnProgress(cts), cts.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.True(d.IsDirty);
        Assert.Equal(3, d.PageCount);
        Assert.Empty(Directory.GetFiles(dir.Path, ".*.tmp"));
        // still usable afterwards
        await d.SaveAsync(path);
        Assert.Equal(new[] { 110, 120, 130 }, await TestPdf.WidthsAsync(path));
    }
}
