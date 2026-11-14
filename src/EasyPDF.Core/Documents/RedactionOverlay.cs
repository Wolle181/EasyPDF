using System.Collections.Generic;
using System.Linq;
using EasyPDF.Core.Ocr;

namespace EasyPDF.Core.Documents;

/// <summary>Colour of a redaction box.</summary>
public enum RedactionFill
{
    /// <summary>Blacked out: shows that something was removed.</summary>
    Black,

    /// <summary>Covered in white: the area looks like empty paper.</summary>
    White,
}

/// <summary>
/// An area of a page to black out or cover in white (not saved yet). Like <see cref="StampOverlay"/> the rectangle lives
/// in the page's own space (source page size after its own /Rotate, before the extra rotation, origin top-left, in points).
/// When saving, everything under it is removed from the file, not just hidden (<see cref="PageRedactor"/>).
/// </summary>
public sealed record RedactionOverlay(int Id, RectPt Rect, RedactionFill Fill);

public static class Redaction
{
    /// <summary>True if the two rects share an area (touching edges do not count).</summary>
    public static bool Intersects(RectPt a, RectPt b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    /// <summary>True if <paramref name="box"/> (page space) reaches into one of the redactions.</summary>
    public static bool Covers(IReadOnlyList<RedactionOverlay> redactions, RectPt box)
    {
        foreach (var r in redactions)
            if (Intersects(r.Rect, box))
                return true;
        return false;
    }

    /// <summary>The words (page space) that no redaction reaches into: a partly covered word is left out as a whole.</summary>
    public static IReadOnlyList<OcrWord> VisibleWords(IReadOnlyList<OcrWord> words, IReadOnlyList<RedactionOverlay> redactions) =>
        redactions.Count == 0 ? words : [.. words.Where(w => !Covers(redactions, w.Box))];
}
