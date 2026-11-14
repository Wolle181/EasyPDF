using EasyPDF.Core.Documents;
using Xunit;

namespace EasyPDF.Core.Tests;

public class PrintRangeTests
{
    [Fact]
    public void ResolvesCurrentRangeAndAll()
    {
        Assert.Equal([4], PrintRange.Resolve(PrintScope.CurrentPage, 10, 4, null));
        Assert.Equal([2, 3, 4], PrintRange.Resolve(PrintScope.Range, 10, 0, "3-5"));
        Assert.Equal([0, 2, 4], PrintRange.Resolve(PrintScope.Range, 10, 0, "1, 3, 5"));
        Assert.Equal([9], PrintRange.Resolve(PrintScope.Range, 10, 0, "10"));
        Assert.Equal([0, 1, 2], PrintRange.Resolve(PrintScope.AllPages, 3, 1, null));
    }

    [Theory]
    [InlineData(PrintScope.Range, 10, 0, "5-3")]
    [InlineData(PrintScope.Range, 10, 0, "0-3")]
    [InlineData(PrintScope.Range, 10, 0, "8-11")]
    [InlineData(PrintScope.Range, 10, 0, "")]
    [InlineData(PrintScope.CurrentPage, 10, 10, null)]
    [InlineData(PrintScope.AllPages, 0, 0, null)]
    public void RejectsInvalidInput(PrintScope scope, int pages, int current, string? range) =>
        Assert.Null(PrintRange.Resolve(scope, pages, current, range));
}
