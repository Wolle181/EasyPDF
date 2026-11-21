using System.Globalization;
using Avalonia.Media;

namespace EasyPDF.App.Services;

/// <summary>How placed text is shown on screen: the PDF standard fonts mapped to similar system fonts, and text measuring.</summary>
public static class TextMetrics
{
    /// <summary>
    /// A folder of the program's resources with Liberation Sans, Serif and Mono (e.g. "avares://EasyPDF.Browser/Assets/Fonts"),
    /// used before the system's fonts (web version: a browser has no system fonts); null on the desktop.
    /// </summary>
    public static string? EmbeddedFonts { get; set; }

    public static FontFamily Family(string font)
    {
        var (names, embedded) = font switch
        {
            "Times" => ("Times New Roman, Times, Liberation Serif, DejaVu Serif", "Liberation Serif"),
            "Courier" => ("Courier New, Courier, Liberation Mono, DejaVu Sans Mono", "Liberation Mono"),
            _ => ("Arial, Helvetica, Liberation Sans, DejaVu Sans", "Liberation Sans"),
        };
        return new FontFamily(EmbeddedFonts is { } folder ? $"{folder}#{embedded}, {names}" : names);
    }

    public static FormattedText Format(string text, string font, double sizePt, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Family(font)), sizePt, brush);

    /// <summary>Width and height in points of the text at the given size (no padding).</summary>
    public static (double Width, double Height) Measure(string text, string font, double sizePt)
    {
        var formatted = Format(text, font, sizePt, Brushes.Black);
        return (formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }
}
