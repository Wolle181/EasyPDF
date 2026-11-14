using System.Collections.Generic;
using System.Globalization;

namespace EasyPDF.Core.Documents;

public enum PageRangeError
{
    None,
    Empty,

    /// <summary>A token is not a number or "a-b".</summary>
    Syntax,

    /// <summary>Page number below 1 or above the page count.</summary>
    OutOfRange,

    /// <summary>"5-3": start is greater than end.</summary>
    ReversedRange,
}

/// <summary>
/// Result of parsing a page range. The UI turns <see cref="Error"/> + <see cref="Token"/> + <see cref="PageCount"/>
/// into a localized message, so no UI text lives here.
/// </summary>
public sealed class PageRangeResult
{
    public PageRangeError Error { get; init; }

    /// <summary>The offending part of the input (trimmed), if any.</summary>
    public string? Token { get; init; }

    public int PageCount { get; init; }

    /// <summary>Zero-based, sorted ascending, without duplicates.</summary>
    public IReadOnlyList<int> Pages { get; init; } = [];

    public bool Success => Error == PageRangeError.None;
}

/// <summary>Parses user input like "1-3, 5, 8-10" (1-based) into zero-based page indices.</summary>
public static class PageRangeParser
{
    public static PageRangeResult Parse(string? text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Fail(PageRangeError.Empty, null, pageCount);

        var pages = new SortedSet<int>();
        foreach (var raw in text.Split(',', ';'))
        {
            var token = raw.Trim();
            if (token.Length == 0)
                return Fail(PageRangeError.Syntax, token, pageCount);

            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (!TryNumber(token, out var n))
                    return Fail(PageRangeError.Syntax, token, pageCount);
                if (n < 1 || n > pageCount)
                    return Fail(PageRangeError.OutOfRange, token, pageCount);
                pages.Add(n - 1);
                continue;
            }

            if (!TryNumber(token[..dash], out var from) || !TryNumber(token[(dash + 1)..], out var to))
                return Fail(PageRangeError.Syntax, token, pageCount);
            if (from < 1 || to < 1 || from > pageCount || to > pageCount)
                return Fail(PageRangeError.OutOfRange, token, pageCount);
            if (from > to)
                return Fail(PageRangeError.ReversedRange, token, pageCount);
            for (var i = from; i <= to; i++)
                pages.Add(i - 1);
        }

        return new PageRangeResult { Pages = [.. pages], PageCount = pageCount };
    }

    /// <summary>Zero-based page indices as text the parser reads back, e.g. "1-3, 5" (runs of three or more pages as a range).</summary>
    public static string Format(IEnumerable<int> pages)
    {
        var parts = new List<string>();
        var sorted = new List<int>(new SortedSet<int>(pages));
        for (var i = 0; i < sorted.Count;)
        {
            var end = i;
            while (end + 1 < sorted.Count && sorted[end + 1] == sorted[end] + 1)
                end++;
            if (end - i >= 2)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{sorted[i] + 1}-{sorted[end] + 1}"));
                i = end + 1;
            }
            else
            {
                parts.Add((sorted[i] + 1).ToString(CultureInfo.InvariantCulture));
                i++;
            }
        }
        return string.Join(", ", parts);
    }

    private static bool TryNumber(string s, out int value) =>
        int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static PageRangeResult Fail(PageRangeError error, string? token, int pageCount) =>
        new() { Error = error, Token = token, PageCount = pageCount };
}
