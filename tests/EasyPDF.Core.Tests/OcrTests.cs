using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

/// <summary>Loads the OCR models once for all tests of the class (about 0.3 s).</summary>
public sealed class OcrEngineFixture : IDisposable
{
    public RapidOcrEngine Engine { get; } = new();

    public void Dispose() => Engine.Dispose();
}

/// <summary>
/// Round trip: pdfium writes a PDF with known text, OCR reads the rendered page, and the words and their positions are
/// compared with what pdfium reports for its own text layer.
/// </summary>
public class OcrTests(OcrEngineFixture fixture) : IClassFixture<OcrEngineFixture>
{
    private static readonly string[] Lines =
    [
        "Sehr geehrte Frau Müller,",
        "vielen Dank für Ihre Anfrage über 250 Stück.",
        "Die Lieferung erfolgt frei Haus; Größe und Gewicht",
        "stehen im Prüfprotokoll. Mit freundlichen Grüßen",
        "The quick brown fox jumps over the lazy dog.",
        "Invoice number 4711, total amount 1.234,56 EUR",
    ];

    /// <summary>A4 page 0 upright, page 1 with /Rotate 90 (landscape as shown, text upright as shown), page 2 blank.</summary>
    private static async Task<string> WriteLetterAsync(TempDir dir)
    {
        var path = Path.Combine(dir.Path, "letter.pdf");
        TestPdf.Create(path, [(595, 842, 0), (595, 842, 90), (595, 842, 0)]);
        await using var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        await using var doc = EditableDocument.FromSource(source);
        for (var page = 0; page < 2; page++)
            for (var i = 0; i < Lines.Length; i++)
                doc.AddText(page, Lines[i], "Helvetica", 12, "#000000", new RectPt(60, 80 + 24 * i, 440, 16));
        var output = dir.File("letter-text.pdf");
        await doc.SaveAsync(output);
        return output;
    }

    private static string Norm(string w) => w.Trim(".,;:!?".ToCharArray()).ToLowerInvariant();

    private static double Overlap(double a0, double a1, double b0, double b1)
    {
        var inter = Math.Min(a1, b1) - Math.Max(a0, b0);
        return inter <= 0 ? 0 : inter / (Math.Max(a1, b1) - Math.Min(a0, b0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RecognizedWordsMatchTheTextLayerInTextAndPosition(int pageIndex)
    {
        using var dir = new TempDir();
        await using var source = await PdfSource.OpenAsync(await WriteLetterAsync(dir));
        var size = await source.GetPageSizeAsync(pageIndex);
        var truth = await source.ReadWordsAsync(pageIndex);
        var ocr = await PageOcr.RecognizeAsync(fixture.Engine, source, pageIndex);

        // The text layer itself: every written word, horizontal as shown, inside the page
        Assert.Equal(Lines.Sum(l => l.Split(' ').Length), truth.Count);
        Assert.All(truth, w => Assert.True(w.Box.Width > w.Box.Height && w.Box.X >= 0 && w.Box.Right <= size.Width && w.Box.Bottom <= size.Height, w.ToString()));

        // Text: nearly all words, umlauts exact
        var found = ocr.Select(w => Norm(w.Text)).ToList();
        var missing = truth.Select(w => Norm(w.Text)).Where(t => !found.Remove(t)).ToList();
        Assert.True(missing.Count <= 2, "missing: " + string.Join(", ", missing) + " | ocr: " + string.Join(" ", ocr.Select(w => w.Text)));
        foreach (var word in new[] { "Müller", "für", "über", "Stück", "Größe", "Prüfprotokoll", "Grüßen" })
            Assert.Contains(ocr, w => w.Text.Trim(".,;:".ToCharArray()) == word);

        // Position: words that occur once lie on their line and overlap their true box horizontally. Word boxes of the
        // recognizer are estimated inside the line (short words are off by a few points), so single words are checked
        // loosely and the median strictly; a mapping error would shift all of them.
        var unique = truth.GroupBy(w => Norm(w.Text)).Where(g => g.Count() == 1 && g.Key.Length >= 3).Select(g => g.Single()).ToList();
        var pairs = unique.Select(t => (Truth: t.Box, Ocr: ocr.Where(w => Norm(w.Text) == Norm(t.Text)).Select(w => w.Box).ToList()))
            .Where(p => p.Ocr.Count == 1).Select(p => (p.Truth, Ocr: p.Ocr[0])).ToList();
        Assert.True(pairs.Count >= unique.Count * 0.9, $"{pairs.Count} of {unique.Count}");
        foreach (var (t, o) in pairs)
        {
            Assert.True(Math.Abs(o.CenterY - t.CenterY) < 6, $"other line: {o} vs. {t}"); // lines are 24 pt apart
            Assert.True(Overlap(o.X, o.Right, t.X, t.Right) > 0.4, $"{o} vs. {t}");
        }
        static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }
        Assert.True(Median(pairs.Select(p => Overlap(p.Ocr.X, p.Ocr.Right, p.Truth.X, p.Truth.Right))) > 0.8);
        Assert.InRange(Median(pairs.Select(p => p.Ocr.CenterX - p.Truth.CenterX)), -1.5, 1.5);
        Assert.InRange(Median(pairs.Select(p => p.Ocr.CenterY - p.Truth.CenterY)), -2.5, 2.5);
        Assert.InRange(Median(pairs.Select(p => p.Ocr.Height)), 12, 24); // about the line height of 12 pt text
        Assert.All(ocr, w => Assert.InRange(w.Confidence, 0f, 1f));
    }

    [Fact]
    public async Task ABlankPageHasNoTextAndNoWords()
    {
        using var dir = new TempDir();
        await using var source = await PdfSource.OpenAsync(await WriteLetterAsync(dir));
        Assert.True(await source.CountTextCharsAsync(0) > 100);
        Assert.Equal(0, await source.CountTextCharsAsync(2));
        Assert.Empty(await source.ReadWordsAsync(2));
        Assert.Empty(await PageOcr.RecognizeAsync(fixture.Engine, source, 2));
    }

    [Fact]
    public async Task ACancelledRecognitionThrows()
    {
        using var dir = new TempDir();
        await using var source = await PdfSource.OpenAsync(await WriteLetterAsync(dir));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PageOcr.RecognizeAsync(fixture.Engine, source, 0, token: cts.Token));
    }

    [Fact]
    public void LargePagesAreRenderedWithFewerDotsPerInch()
    {
        Assert.Equal((2480, 3508), PageOcr.PixelSize(new PageSizePt(595.276, 841.89))); // A4 at 300 dpi
        var (w, h) = PageOcr.PixelSize(new PageSizePt(2384, 3370)); // A0
        Assert.Equal(PageOcr.MaxPixels, h);
        Assert.InRange(w, 4940, 4960);
    }

    [Fact]
    public void AMissingModelFolderIsReported()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => new RapidOcrEngine(Path.Combine(Path.GetTempPath(), "no-such-ocr-models")));
        Assert.Contains("no-such-ocr-models", ex.FileName);
    }

    [Fact]
    public void TheModelsAreCopiedNextToTheProgram()
    {
        var files = Directory.GetFiles(RapidOcrEngine.DefaultModelDirectory).Select(Path.GetFileName).ToList();
        Assert.Contains("latin_PP-OCRv5_rec_mobile_infer.onnx", files);
        Assert.Contains("ppocrv5_latin_dict.txt", files);
    }
}
