using System;
using EasyPDF.Core.Settings;

namespace EasyPDF.Core.Fonts;

/// <summary>
/// The font most of a page's visible text is written in (by number of characters, see
/// <c>PdfSource.ReadMainFontAsync</c>): its name in the PDF (<paramref name="BaseFont"/>, usually the PostScript name,
/// a subset prefix like "ABCDEF+" already removed), the family pdfium reports, the size in points as it appears on the
/// page, and the style from its descriptor and name.
/// </summary>
public sealed record PageFont(string BaseFont, string Family, double Size, bool Bold, bool Italic, bool Serif, bool FixedPitch)
{
    /// <summary>The name without the "ABCDEF+" prefix of a subset font.</summary>
    public static string WithoutSubsetPrefix(string name) =>
        name.Length > 7 && name[6] == '+' && name.AsSpan(0, 6).IndexOfAnyExceptInRange('A', 'Z') < 0 ? name[7..] : name;

    /// <summary>Bold or italic as far as the font's name tells (e.g. "Arial-BoldItalicMT", "Calibri,Bold", "Helvetica-Oblique").</summary>
    public static (bool Bold, bool Italic) StyleFromName(string name)
    {
        var dash = name.IndexOfAny(['-', ',']);
        var style = dash >= 0 ? name[(dash + 1)..] : "";
        bool Has(string word) => style.Contains(word, StringComparison.OrdinalIgnoreCase);
        return (Has("Bold") || Has("Black") || Has("Heavy") || Has("Semibold") || Has("Demi"),
            Has("Italic") || Has("Oblique") || Has("Kursiv"));
    }

    /// <summary>
    /// The pen for new text in this font: the installed font of that name when <paramref name="fonts"/> has it (it is
    /// embedded when saving), otherwise the most similar standard font (monospaced → Courier, serif → Times, else
    /// Helvetica) in the same style. The size is kept, within the sizes text may have.
    /// </summary>
    public TextPen ToPen(FontLibrary? fonts)
    {
        var size = Math.Round(TextStyle.ClampSize(Size) * 2) / 2;
        if (TextStyle.StandardFor(BaseFont) is { } standard)
            return new TextPen(standard, size, Bold, Italic);
        if (fonts?.Match(BaseFont, Family, Bold, Italic) is { } face)
            return new TextPen(face.Family, size, face.Bold, face.Italic);
        var fallback = FixedPitch ? "Courier" : Serif ? "Times" : TextStyle.SimilarStandard(BaseFont + " " + Family);
        return new TextPen(fallback, size, Bold, Italic);
    }
}

/// <summary>Font, size and style of new text: <paramref name="Font"/> is a standard font (<see cref="TextStyle.Fonts"/>) or the family of an installed font.</summary>
public sealed record TextPen(string Font, double Size, bool Bold = false, bool Italic = false);
