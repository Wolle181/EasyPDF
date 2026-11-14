using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

/// <summary>Finding text in the words of a page (search bar).</summary>
public class TextSearchTests
{
    private static OcrWord W(string text, double x, double y, double w, int rotation = 0) => new(text, new RectPt(x, y, w, 10), 1, rotation);

    private static readonly OcrWord[] Line = [W("Annual", 10, 20, 60), W("Report", 80, 20, 60), W("2025", 150, 20, 40), W("report", 10, 40, 60)];

    [Theory]
    [InlineData("  annual \t report ", "annual report")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("a\nb", "a b")]
    public void QueriesAreTrimmedAndWhiteSpaceBecomesOneSpace(string? query, string expected) =>
        Assert.Equal(expected, TextSearch.NormalizeQuery(query));

    [Fact]
    public void WholeWordsAreFoundIgnoringCase()
    {
        var hits = TextSearch.Find(Line, "REPORT");
        Assert.Equal(2, hits.Count);
        Assert.Equal(Line[1].Box, Assert.Single(hits[0]));
        Assert.Equal(Line[3].Box, Assert.Single(hits[1]));
    }

    [Fact]
    public void SeveralWordsAreFoundAcrossWordsAndLines()
    {
        var hit = Assert.Single(TextSearch.Find(Line, "2025 report"));
        Assert.Equal([Line[2].Box, Line[3].Box], hit);
    }

    [Fact]
    public void APartOfAWordGetsThePartOfItsBox()
    {
        // "port" is the last 4 of 6 letters of "Report" (80..140): 100..140
        var hit = Assert.Single(TextSearch.Find([Line[1]], "port"));
        var box = Assert.Single(hit);
        Assert.Equal(100, box.X, 6);
        Assert.Equal(40, box.Width, 6);
        Assert.Equal(20, box.Y);

        // Across two words: the end of the first and the start of the second
        var across = Assert.Single(TextSearch.Find(Line, "ual rep"));
        Assert.Equal(2, across.Count);
        Assert.Equal(40, across[0].X, 6);  // "ual" = letters 3..6 of "Annual" (10..70)
        Assert.Equal(30, across[0].Width, 6);
        Assert.Equal(80, across[1].X, 6);  // "Rep" = letters 0..3 of "Report"
        Assert.Equal(30, across[1].Width, 6);
    }

    [Fact]
    public void TurnedWordsAreCutAlongTheirDirection()
    {
        // Text running down the page (90°): the second half of the letters is the lower half of the box
        var word = new OcrWord("abcd", new RectPt(10, 100, 10, 40), 1, 90);
        var box = Assert.Single(Assert.Single(TextSearch.Find([word], "cd")));
        Assert.Equal(new RectPt(10, 120, 10, 20), box);
        // Running up (270°): the second half is the upper half
        box = Assert.Single(Assert.Single(TextSearch.Find([word with { Rotation = 270 }], "cd")));
        Assert.Equal(new RectPt(10, 100, 10, 20), box);
    }

    [Fact]
    public void NoQueryOrNoMatchFindsNothingAndHitsDoNotOverlap()
    {
        Assert.Empty(TextSearch.Find(Line, ""));
        Assert.Empty(TextSearch.Find(Line, "energy"));
        Assert.Empty(TextSearch.Find([], "a"));
        Assert.Equal(2, TextSearch.Find([W("aaaa", 0, 0, 40)], "aa").Count);
    }

    [Fact]
    public async Task TheTextLayerOfTheDemoReportIsSearchable()
    {
        var dir = System.AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "EasyPDF.slnx")))
            dir = Path.GetDirectoryName(dir);
        await using var source = await PdfSource.OpenAsync(Path.Combine(dir!, "demo-pdfs", "Annual Report 2025.pdf"));
        var document = EditableDocument.FromSource(source);
        var words = await PageText.ReadWordsAsync(document.Pages[0]);
        var hits = TextSearch.Find(words, TextSearch.NormalizeQuery("annual report"));
        Assert.NotEmpty(hits);
        var size = await source.GetPageSizeAsync(0);
        Assert.All(hits.SelectMany(h => h), box => Assert.True(box.X >= 0 && box.Right <= size.Width && box.Y >= 0 && box.Bottom <= size.Height));
    }
}
