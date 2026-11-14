using System.Collections.Generic;
using System.Linq;

namespace EasyPDF.Core.Documents;

public enum PrintScope
{
    CurrentPage,
    Range,
    AllPages,
}

/// <summary>Which pages a print job covers.</summary>
public static class PrintRange
{
    /// <summary>
    /// Zero-based pages to print, or null if the input is invalid (range outside the document or reversed).
    /// <paramref name="current"/> is zero-based, <paramref name="range"/> is the text the user typed, e.g. "1-3, 5" (<see cref="PageRangeParser"/>).
    /// </summary>
    public static IReadOnlyList<int>? Resolve(PrintScope scope, int pageCount, int current, string? range)
    {
        if (pageCount < 1)
            return null;
        switch (scope)
        {
            case PrintScope.CurrentPage:
                return current >= 0 && current < pageCount ? [current] : null;
            case PrintScope.Range:
                return PageRangeParser.Parse(range, pageCount) is { Success: true } parsed ? parsed.Pages : null;
            default:
                return [.. Enumerable.Range(0, pageCount)];
        }
    }
}
