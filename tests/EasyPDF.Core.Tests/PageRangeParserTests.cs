using System.Linq;
using EasyPDF.Core.Documents;
using Xunit;

namespace EasyPDF.Core.Tests;

public class PageRangeParserTests
{
    [Theory]
    [InlineData("1-3, 5, 8-10", 10, new[] { 0, 1, 2, 4, 7, 8, 9 })]
    [InlineData("  2 ", 5, new[] { 1 })]
    [InlineData("3,1,2", 5, new[] { 0, 1, 2 })]
    [InlineData("1-3,2-4,3", 5, new[] { 0, 1, 2, 3 })]
    [InlineData("1 - 2 ; 5", 5, new[] { 0, 1, 4 })]
    [InlineData("4-4", 5, new[] { 3 })]
    public void Valid(string text, int pageCount, int[] expected)
    {
        var r = PageRangeParser.Parse(text, pageCount);
        Assert.True(r.Success);
        Assert.Equal(expected, r.Pages);
    }

    [Theory]
    [InlineData("", PageRangeError.Empty)]
    [InlineData("   ", PageRangeError.Empty)]
    [InlineData("a", PageRangeError.Syntax)]
    [InlineData("1,,2", PageRangeError.Syntax)]
    [InlineData("1-", PageRangeError.Syntax)]
    [InlineData("-3", PageRangeError.Syntax)]
    [InlineData("1-2-3", PageRangeError.Syntax)]
    [InlineData("0", PageRangeError.OutOfRange)]
    [InlineData("6", PageRangeError.OutOfRange)]
    [InlineData("3-9", PageRangeError.OutOfRange)]
    [InlineData("5-3", PageRangeError.ReversedRange)]
    public void Invalid(string text, PageRangeError error)
    {
        var r = PageRangeParser.Parse(text, 5);
        Assert.False(r.Success);
        Assert.Equal(error, r.Error);
        Assert.Empty(r.Pages);
    }

    [Fact]
    public void ErrorReportsOffendingToken()
    {
        var r = PageRangeParser.Parse("1-2, 9", 5);
        Assert.Equal("9", r.Token);
        Assert.Equal(5, r.PageCount);
    }

    [Theory]
    [InlineData(new int[] { 0 }, "1")]
    [InlineData(new int[] { 0, 1 }, "1, 2")]
    [InlineData(new int[] { 0, 1, 2 }, "1-3")]
    [InlineData(new int[] { 4, 0, 2, 1 }, "1-3, 5")]
    [InlineData(new int[] { 0, 2, 4 }, "1, 3, 5")]
    [InlineData(new int[] { 0, 1, 2, 5, 7, 8, 9 }, "1-3, 6, 8-10")]
    public void FormatsPagesAsTextTheParserReadsBack(int[] pages, string text)
    {
        Assert.Equal(text, PageRangeParser.Format(pages));
        Assert.Equal(pages.OrderBy(p => p), PageRangeParser.Parse(text, 20).Pages);
    }
}
