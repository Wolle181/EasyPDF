using System.Globalization;
using Avalonia.Media;
using EasyPDF.Core.Settings;

namespace EasyPDF.App.Services;

/// <summary>
/// How placed text is shown on screen: the PDF standard fonts mapped to similar system fonts, an installed font
/// (<c>FontLibrary</c>, embedded when saving) by its family name with the similar standard font behind it, and text measuring.
/// </summary>
public static class TextMetrics
{
    /// <summary>
    /// A folder of the program's resources with Liberation Sans, Serif and Mono (e.g. "avares://EasyPDF.Browser/Assets/Fonts"),
    /// used before the system's fonts (web version: a browser has no system fonts); null on the desktop.
    /// </summary>
    public static string? EmbeddedFonts { get; set; }

    public static FontFamily Family(string font)
    {
        var standard = TextStyle.IsStandard(font) ? TextStyle.NormalizeFont(font) : TextStyle.SimilarStandard(font);
        var (names, embedded) = standard switch
        {
            "Times" => ("Times New Roman, Times, Liberation Serif, DejaVu Serif", "Liberation Serif"),
            "Courier" => ("Courier New, Courier, Liberation Mono, DejaVu Sans Mono", "Liberation Mono"),
            _ => ("Arial, Helvetica, Liberation Sans, DejaVu Sans", "Liberation Sans"),
        };
        var similar = EmbeddedFonts is { } folder ? $"{folder}#{embedded}, {names}" : names;
        // An installed font first; where it is missing the similar standard font, as in the saved file
        return new FontFamily(TextStyle.IsStandard(font) ? similar : $"{font.Replace(",", "")}, {similar}");
    }

    public static Typeface Typeface(string font, bool bold = false, bool italic = false) =>
        new(Family(font), italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal);

    public static FormattedText Format(string text, string font, double sizePt, IBrush brush, bool bold = false, bool italic = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface(font, bold, italic), sizePt, brush);

    /// <summary>Width and height in points of the text at the given size (no padding).</summary>
    public static (double Width, double Height) Measure(string text, string font, double sizePt, bool bold = false, bool italic = false)
    {
        var formatted = Format(text, font, sizePt, Brushes.Black, bold, italic);
        return (formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }
}
