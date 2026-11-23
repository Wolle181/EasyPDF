using System;
using System.Globalization;
using System.Linq;

namespace EasyPDF.Core.Settings;

/// <summary>Choices and validation for the style of inserted text (pen colour, font, size).</summary>
public static class TextStyle
{
    public const string DefaultColor = "#000000";

    public const double MinSize = 4;
    public const double MaxSize = 200;

    /// <summary>The standard PDF fonts: they exist in every viewer without embedding.</summary>
    public static readonly string[] Fonts = ["Helvetica", "Times", "Courier"];

    /// <summary>Sizes offered in the size list.</summary>
    public static readonly double[] Sizes = [8, 9, 10, 11, 12, 14, 16, 18, 24, 36, 48, 72];

    /// <summary>Colours offered as swatches (#RRGGBB).</summary>
    public static readonly string[] Palette =
    [
        "#000000", "#595959", "#A6A6A6", "#FFFFFF",
        "#C00000", "#FF6600", "#FFC000", "#92D050",
        "#00A651", "#00B0F0", "#0070C0", "#002060",
        "#7030A0", "#FF00FF", "#8B4513", "#008080",
    ];

    /// <summary>Returns the colour as upper-case #RRGGBB, or the default if it is not a valid colour (3 or 6 hex digits, # optional).</summary>
    public static string NormalizeColor(string? text)
    {
        var t = (text ?? "").Trim().TrimStart('#');
        if (t.Length == 3 && t.All(Uri.IsHexDigit))
            t = string.Concat(t.Select(c => $"{c}{c}"));
        return t.Length == 6 && t.All(Uri.IsHexDigit) ? "#" + t.ToUpperInvariant() : DefaultColor;
    }

    public static bool IsValidColor(string? text)
    {
        var t = (text ?? "").Trim().TrimStart('#');
        return (t.Length is 3 or 6) && t.All(Uri.IsHexDigit);
    }

    /// <summary>Red, green and blue (0..255) of a colour string.</summary>
    public static (byte R, byte G, byte B) ToRgb(string? text)
    {
        var c = NormalizeColor(text);
        return (byte.Parse(c[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(c[3..5], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(c[5..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static string NormalizeFont(string? font) =>
        Fonts.FirstOrDefault(f => string.Equals(f, font, StringComparison.OrdinalIgnoreCase)) ?? Fonts[0];

    /// <summary>True for one of the standard fonts (else it is the family of an installed font, embedded when saving).</summary>
    public static bool IsStandard(string? font) => Fonts.Any(f => string.Equals(f, font, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The standard font a PDF font name stands for when it is one of the base fonts Helvetica, Times or Courier (in
    /// any style, e.g. "Times-Bold"); null for any other font.
    /// </summary>
    public static string? StandardFor(string pdfName)
    {
        var name = pdfName.Split('-', ',')[0];
        return name switch
        {
            "Helvetica" => "Helvetica",
            "Times" or "Times-Roman" or "TimesRoman" => "Times",
            "Courier" => "Courier",
            _ => null,
        };
    }

    /// <summary>The standard font that looks most like a font of that name: Courier for monospaced, Times for serif names, else Helvetica.</summary>
    public static string SimilarStandard(string name)
    {
        bool Has(string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);
        if (Has("Courier") || Has("Mono") || Has("Consol") || Has("Typewriter"))
            return "Courier";
        if (Has("Times") || Has("Roman") || Has("Serif") && !Has("Sans") || Has("Georgia") || Has("Garamond") || Has("Cambria")
            || Has("Book") || Has("Palatino") || Has("Minion") || Has("Baskerville"))
            return "Times";
        return "Helvetica";
    }

    /// <summary>The name of a standard font in a style as PDF knows it (e.g. "Times-BoldItalic", "Helvetica-Oblique").</summary>
    public static string StandardPdfName(string font, bool bold, bool italic) => NormalizeFont(font) switch
    {
        "Times" => bold ? (italic ? "Times-BoldItalic" : "Times-Bold") : italic ? "Times-Italic" : "Times-Roman",
        "Courier" => bold ? (italic ? "Courier-BoldOblique" : "Courier-Bold") : italic ? "Courier-Oblique" : "Courier",
        _ => bold ? (italic ? "Helvetica-BoldOblique" : "Helvetica-Bold") : italic ? "Helvetica-Oblique" : "Helvetica",
    };

    public static double ClampSize(double size) =>
        double.IsNaN(size) ? 12 : Math.Clamp(size, MinSize, MaxSize);
}
