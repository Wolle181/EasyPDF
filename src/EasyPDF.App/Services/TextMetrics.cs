using System.Globalization;
using Avalonia.Media;

namespace EasyPDF.App.Services;

/// <summary>How placed text is shown on screen: the PDF standard fonts mapped to similar system fonts, and text measuring.</summary>
public static class TextMetrics
{
    public static FontFamily Family(string font) => font switch
    {
        "Times" => new FontFamily("Times New Roman, Times, Liberation Serif, DejaVu Serif"),
        "Courier" => new FontFamily("Courier New, Courier, Liberation Mono, DejaVu Sans Mono"),
        _ => new FontFamily("Arial, Helvetica, Liberation Sans, DejaVu Sans"),
    };

    public static FormattedText Format(string text, string font, double sizePt, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Family(font)), sizePt, brush);

    /// <summary>Width and height in points of the text at the given size (no padding).</summary>
    public static (double Width, double Height) Measure(string text, string font, double sizePt)
    {
        var formatted = Format(text, font, sizePt, Brushes.Black);
        return (formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }
}
