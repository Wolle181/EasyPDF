using System;
using System.Collections.Generic;
using System.Text;
using EasyPDF.Core.Documents;

namespace EasyPDF.Core.Ocr;

/// <summary>A place in the document where the searched text was found.</summary>
/// <param name="PageIndex">Index of the page when it was searched.</param>
/// <param name="Page">The page as it was searched; a changed page is a new entry, so its old hits no longer apply.</param>
/// <param name="Boxes">One box per word the hit touches (only the matching part of the word), in the page's own space like <see cref="OcrWord.Box"/>.</param>
public sealed record TextSearchHit(int PageIndex, PageEntry Page, IReadOnlyList<RectPt> Boxes);

/// <summary>
/// Finds text in the words of a page (recognized words or the text layer of the file). The words are joined with single
/// spaces, so a search for several words also finds them across line ends; upper and lower case are the same.
/// </summary>
public static class TextSearch
{
    /// <summary>The query as it is searched: trimmed, any run of white space as one space.</summary>
    public static string NormalizeQuery(string? query)
    {
        var sb = new StringBuilder();
        var space = false;
        foreach (var c in query ?? "")
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The places where <paramref name="query"/> (already normalized, see <see cref="NormalizeQuery"/>) occurs in
    /// <paramref name="words"/>, in reading order; each as the boxes of the matching parts of the words it touches.
    /// Hits do not overlap.
    /// </summary>
    public static List<IReadOnlyList<RectPt>> Find(IReadOnlyList<OcrWord> words, string query)
    {
        var hits = new List<IReadOnlyList<RectPt>>();
        if (query.Length == 0 || words.Count == 0)
            return hits;

        // The page text: words separated by one space; starts[i] is where word i begins
        var sb = new StringBuilder();
        var starts = new int[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0)
                sb.Append(' ');
            starts[i] = sb.Length;
            sb.Append(words[i].Text);
        }
        // ToUpperInvariant maps char by char, so positions stay the same in both texts
        var text = sb.ToString().ToUpperInvariant();
        var wanted = query.ToUpperInvariant();

        for (var at = text.IndexOf(wanted, StringComparison.Ordinal); at >= 0; at = text.IndexOf(wanted, at + wanted.Length, StringComparison.Ordinal))
        {
            var end = at + wanted.Length; // exclusive
            var boxes = new List<RectPt>();
            for (var w = WordAt(starts, at); w < words.Count && starts[w] < end; w++)
            {
                var length = words[w].Text.Length;
                var from = Math.Max(0, at - starts[w]);
                var to = Math.Min(length, end - starts[w]);
                if (to > from)
                    boxes.Add(Part(words[w], from, to));
            }
            if (boxes.Count > 0)
                hits.Add(boxes);
        }
        return hits;
    }

    /// <summary>The word that contains (or, at a space, follows) text position <paramref name="position"/>.</summary>
    private static int WordAt(int[] starts, int position)
    {
        var index = Array.BinarySearch(starts, position);
        if (index >= 0)
            return index;
        index = ~index - 1; // the word starting before the position
        return Math.Max(0, index);
    }

    /// <summary>
    /// The part of a word's box that holds its characters <paramref name="from"/>..<paramref name="to"/> (exclusive),
    /// assuming characters of equal width along the direction of the text.
    /// </summary>
    private static RectPt Part(OcrWord word, int from, int to)
    {
        var length = word.Text.Length;
        if (length == 0 || (from == 0 && to == length))
            return word.Box;
        double a = (double)from / length, b = (double)to / length;
        var box = word.Box;
        return (((word.Rotation % 360) + 360) % 360) switch
        {
            90 => new RectPt(box.X, box.Y + a * box.Height, box.Width, (b - a) * box.Height),       // running down
            180 => new RectPt(box.X + (1 - b) * box.Width, box.Y, (b - a) * box.Width, box.Height),  // upside down: right to left
            270 => new RectPt(box.X, box.Y + (1 - b) * box.Height, box.Width, (b - a) * box.Height), // running up
            _ => new RectPt(box.X + a * box.Width, box.Y, (b - a) * box.Width, box.Height),
        };
    }
}
