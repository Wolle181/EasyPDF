using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

/// <summary>Recognized words in the document model (undo/redo) and as invisible text layer in saved files.</summary>
public class OcrSaveTests(OcrEngineFixture fixture) : IClassFixture<OcrEngineFixture>
{
    private static readonly OcrWord[] Words =
    [
        new("Hello", new RectPt(20, 20, 50, 16), 0.9f),
        new("Wörter", new RectPt(90, 20, 60, 16), 0.9f),
        new("Grüße", new RectPt(20, 60, 55, 16), 0.9f),
        new("5€", new RectPt(100, 60, 30, 16), 0.9f),
    ];

    private static async Task<EditableDocument> OpenAsync(string path)
    {
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    private static async Task<EditableDocument> CreateAsync(TempDir dir, string name, params (int W, int H, int Rotate)[] pages)
    {
        var path = dir.File(name);
        TestPdf.Create(path, pages);
        return await OpenAsync(path);
    }

    private static Dictionary<int, IReadOnlyList<OcrWord>> On(PageEntry page, IReadOnlyList<OcrWord> words) => new() { [page.Id] = words };

    private static async Task<IReadOnlyList<OcrWord>> ReadAsync(string path, int page)
    {
        await using var s = await PdfSource.OpenAsync(path);
        return await s.ReadWordsAsync(page);
    }

    private static double Overlap(double a0, double a1, double b0, double b1)
    {
        var inter = Math.Min(a1, b1) - Math.Max(a0, b0);
        return inter <= 0 ? 0 : inter / (Math.Max(a1, b1) - Math.Min(a0, b0));
    }

    /// <summary>The saved word lies on the expected box: same text, horizontally covering it, vertically inside it.</summary>
    private static void AssertOnBox(OcrWord saved, string text, RectPt box)
    {
        Assert.Equal(text, saved.Text);
        Assert.True(Overlap(saved.Box.X, saved.Box.Right, box.X, box.Right) > 0.85, $"{text}: {saved.Box} vs. {box}");
        Assert.True(saved.Box.Y >= box.Y - 1 && saved.Box.Bottom <= box.Bottom + 1, $"{text}: {saved.Box} vs. {box}");
        Assert.True(saved.Box.Height > box.Height * 0.3, $"{text}: {saved.Box} vs. {box}");
    }

    private static async Task<bool> IsBlankAsync(string path, int page)
    {
        await using var s = await PdfSource.OpenAsync(path);
        var size = await s.GetPageSizeAsync(page);
        var img = await s.RenderAsync(page, (int)size.Width * 2, (int)size.Height * 2);
        return img.Pixels.All(b => b == 0xFF);
    }

    [Fact]
    public async Task WordsFollowTheirPageAndUndoRedoSwapsThem()
    {
        using var dir = new TempDir();
        await using var doc = await CreateAsync(dir, "a.pdf", (200, 100, 0), (200, 100, 0));
        var second = doc.Pages[1];

        Assert.Equal(0, doc.SetOcrWords(new Dictionary<int, IReadOnlyList<OcrWord>> { [-1] = Words })); // unknown page
        Assert.False(doc.CanUndo);

        doc.Move([1], 0); // OCR finished after the page moved: it still lands on that page
        Assert.Equal(1, doc.SetOcrWords(On(second, Words)));
        Assert.Equal(Words, doc.Pages[0].OcrWords);
        Assert.Empty(doc.Pages[1].OcrWords);
        Assert.Equal(1, doc.OcrPageCount);
        Assert.True(doc.IsDirty);

        doc.Undo();
        Assert.Equal(0, doc.OcrPageCount);
        doc.Redo();
        Assert.Equal(Words, doc.Pages[0].OcrWords);

        doc.Rotate([0], 90); // other edits keep the words
        Assert.Equal(Words, doc.Pages[0].OcrWords);
        Assert.Equal(1, doc.SetOcrWords(On(doc.Pages[0], [Words[0]]))); // a second run replaces the words
        Assert.Single(doc.Pages[0].OcrWords);
    }

    [Fact]
    public async Task SavedWordsAreInvisibleAndLieOnTheirBoxes()
    {
        using var dir = new TempDir();
        await using var doc = await CreateAsync(dir, "a.pdf", (200, 100, 0));
        doc.SetOcrWords(On(doc.Pages[0], Words));
        var path = dir.File("out.pdf");
        await doc.SaveAsync(path);

        Assert.True(await IsBlankAsync(path, 0));
        var saved = await ReadAsync(path, 0);
        Assert.Equal(Words.Length, saved.Count);
        for (var i = 0; i < Words.Length; i++)
            AssertOnBox(saved[i], Words[i].Text, Words[i].Box);
        Assert.Equal(0, doc.OcrPageCount); // reloaded from the saved file
        Assert.True(await doc.Pages[0].Source.CountTextCharsAsync(0) > 0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 0)]
    [InlineData(0, 90)]
    [InlineData(90, 270)]
    [InlineData(0, 180)]
    public async Task WordsOfATurnedPageReadUprightAsShown(int fileRotate, int extraRotation)
    {
        // The page is shown turned by its /Rotate and the editor's extra rotation; OCR saw it upright like that,
        // so the words are turned back by the extra rotation in page space (see PageOcr)
        using var dir = new TempDir();
        await using var doc = await CreateAsync(dir, "a.pdf", (200, 100, fileRotate));
        doc.Rotate([0], extraRotation);
        var baseSize = doc.Pages[0].BaseSize;
        var shownBox = new RectPt(30, 20, 60, 16);
        var word = new OcrWord("Wörter", PageGeometry.Unrotate(shownBox, baseSize, extraRotation), 1, (360 - extraRotation) % 360);
        doc.SetOcrWords(On(doc.Pages[0], [word]));
        var path = dir.File("out.pdf");
        await doc.SaveAsync(path);

        var saved = Assert.Single(await ReadAsync(path, 0));
        AssertOnBox(saved, "Wörter", shownBox); // the saved page is shown like the edited one
    }

    [Fact]
    public async Task ExtractSplitAndConcatenateCarryTheWords()
    {
        using var dir = new TempDir();
        var a = await CreateAsync(dir, "a.pdf", (200, 100, 0), (200, 100, 0));
        var b = await CreateAsync(dir, "b.pdf", (200, 100, 0));
        a.SetOcrWords(On(a.Pages[1], [Words[0]]));
        b.SetOcrWords(On(b.Pages[0], [Words[1]]));

        await PdfSaver.ExtractAsync([a.Pages[1]], dir.File("extract.pdf"));
        AssertOnBox(Assert.Single(await ReadAsync(dir.File("extract.pdf"), 0)), "Hello", Words[0].Box);

        var parts = await PdfSaver.SplitAsync(a.Pages, SplitPlan.EveryN(2, 1), dir.Path, "split");
        Assert.Empty(await ReadAsync(parts[0], 0));
        Assert.Single(await ReadAsync(parts[1], 0));

        await using var combined = EditableDocument.Combine([a, b]); // page 2 is imported from b.pdf
        var path = dir.File("combined.pdf");
        await combined.SaveAsync(path);
        AssertOnBox(Assert.Single(await ReadAsync(path, 1)), "Hello", Words[0].Box);
        AssertOnBox(Assert.Single(await ReadAsync(path, 2)), "Wörter", Words[1].Box);
    }

    [Fact]
    public void CharactersOutsideTheStandardFontEncodingBecomeQuestionMarks()
    {
        Assert.Equal("Grüße €–“x”", PdfSaver.ToWinAnsi(" Grüße €–“x” "));
        Assert.Equal("a?b?", PdfSaver.ToWinAnsi("a→bΩ"));
    }

    /// <summary>
    /// End to end: a text page is turned into a "scan" (only a picture of the page), recognized, saved, and the saved
    /// file has a text layer again that matches the original text in words and position.
    /// </summary>
    [Fact]
    public async Task AScanGetsASearchableTextLayer()
    {
        using var dir = new TempDir();
        // Original with real text
        var original = dir.File("original.pdf");
        TestPdf.Create(original, [(595, 842, 0)]);
        await using (var d = await OpenAsync(original))
        {
            d.AddText(0, "Rechnung über 1.234,56 € für Müller & Söhne\nThe quick brown fox jumps over the lazy dog", "Helvetica", 14, "#000000", new RectPt(60, 100, 460, 40));
            await d.SaveAsync(original);
        }
        IReadOnlyList<OcrWord> truth;
        RenderedPage picture;
        await using (var s = await PdfSource.OpenAsync(original))
        {
            truth = await s.ReadWordsAsync(0);
            picture = await s.RenderAsync(0, 1240, 1754); // 150 dpi
        }

        // The "scan": a blank page with that picture, plus a second page that has text and is skipped
        var scan = dir.File("scan.pdf");
        TestPdf.Create(scan, [(595, 842, 0)]);
        await using (var d = await OpenAsync(scan))
        {
            Assert.Equal(picture.Stride, picture.Width * 4);
            d.AddStamp(0, [], picture.Width, picture.Height, new RectPt(0, 0, 595, 842), 0, picture.Pixels);
            d.InsertFrom([await PdfSource.OpenAsync(original)], 1);
            await d.SaveAsync(scan);
        }

        await using var doc = await OpenAsync(scan);
        Assert.Equal(0, await doc.Pages[0].Source.CountTextCharsAsync(0));
        var progress = new List<int>();
        var found = await PageOcr.RecognizePagesAsync(fixture.Engine, doc.Pages, skipPagesWithText: true, new SyncProgress(progress.Add));
        Assert.Equal([doc.Pages[0].Id], found.Keys);
        Assert.Equal([1, 2], progress);
        doc.SetOcrWords(found);
        var path = dir.File("searchable.pdf");
        await doc.SaveAsync(path);

        var saved = await ReadAsync(path, 0);
        foreach (var t in truth)
        {
            var match = saved.Where(w => w.Text.Trim(',', '.') == t.Text.Trim(',', '.')).ToList();
            Assert.True(match.Count >= 1, $"'{t.Text}' missing in: {string.Join(" ", saved.Select(w => w.Text))}");
            if (t.Text.Length < 2)
                continue; // the recognizer's boxes of single characters are rough
            Assert.True(match.Any(m => Overlap(m.Box.X, m.Box.Right, t.Box.X, t.Box.Right) > 0.5 && Math.Abs(m.Box.CenterY - t.Box.CenterY) < 5),
                $"'{t.Text}' at {t.Box}, saved at {string.Join("; ", match.Select(m => m.Box))}");
        }
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
