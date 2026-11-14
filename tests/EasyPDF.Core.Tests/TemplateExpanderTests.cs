using System;
using System.Globalization;
using EasyPDF.Core.Templates;
using Xunit;

namespace EasyPDF.Core.Tests;

public class TemplateExpanderTests
{
    private static readonly TemplateContext Context = new(new DateTime(2026, 3, 7, 14, 5, 9), "wolf", "Report.final.pdf", 3, 12);
    private static string Expand(string t) => TemplateExpander.Expand(t, Context, CultureInfo.GetCultureInfo("en-GB"));

    [Fact]
    public void KnownMacrosAreReplaced()
    {
        Assert.Equal("07/03/2026", Expand("{$DATE}"));
        Assert.Equal("2026-03-07", Expand("{$DATE:yyyy-MM-dd}"));
        Assert.Equal("14:05", Expand("{$TIME}"));
        Assert.Equal("wolf", Expand("{$USER}"));
        Assert.Equal("Report.final", Expand("{$FILE}"));
        Assert.Equal("Report.final.pdf", Expand("{$FILENAME}"));
        Assert.Equal("Page 3 of 12", Expand("Page {$PAGE} of {$PAGES}"));
        Assert.Equal("a 3 b 3", Expand("a {$page} b {$Page}"));
    }

    [Fact]
    public void UnknownMacrosAndBadFormatsStayAsTyped()
    {
        Assert.Equal("{$NOPE} {$PAGE:x} {$ DATE} {DATE} $PAGE", Expand("{$NOPE} {$PAGE:x} {$ DATE} {DATE} $PAGE"));
        Assert.Equal("x\ny 3", Expand("x\ny {$PAGE}"));
        Assert.Equal("", Expand(""));
    }
}
