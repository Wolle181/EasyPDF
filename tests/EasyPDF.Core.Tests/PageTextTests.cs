using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

/// <summary>Words to plain text lines (copy text to the clipboard).</summary>
public class PageTextTests
{
    private static readonly PageSizePt A = new(200, 100);

    private static OcrWord W(string text, double x, double y, double w = 20, double h = 12) => new(text, new RectPt(x, y, w, h), 1);

    [Fact]
    public void WordsOnOneHeightFormALineAndAWideGapAnEmptyLine()
    {
        var text = PageText.FromWords(
        [
            W("Sehr", 10, 10, 30), W("geehrte", 45, 10, 40),
            W("Damen", 10, 26, 30),
            W("Gruß", 10, 60, 30),
        ], A);
        Assert.Equal("Sehr geehrte\nDamen\n\nGruß", text);
    }

    [Fact]
    public void ASlightlySkewedScanLineStaysOneLine()
    {
        // Each word a little lower than the one before (scan not quite straight)
        var text = PageText.FromWords([W("a", 10, 10), W("b", 40, 13), W("c", 70, 16), W("d", 10, 30)], A);
        Assert.Equal("a b c\nd", text);
    }

    [Fact]
    public void LinesFollowThePageAsShown()
    {
        // In page space the words lie one above the other; turned by 90° (as shown) they read left to right
        OcrWord[] words = [W("eins", 10, 70, 12, 20), W("zwei", 10, 40, 12, 20)];
        Assert.Equal("eins\nzwei", PageText.FromWords(words, A));
        Assert.Equal("eins zwei", PageText.FromWords(words, A, 90));
    }

    [Fact]
    public void NoWordsGiveNoText()
    {
        Assert.Equal("", PageText.FromWords([], A));
        Assert.Equal("", PageText.FromWords([W(" ", 10, 10)], A));
    }
}
