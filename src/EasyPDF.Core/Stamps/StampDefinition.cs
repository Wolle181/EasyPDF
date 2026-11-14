using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using EasyPDF.Core.Templates;

namespace EasyPDF.Core.Stamps;

/// <summary>Where the text of a <see cref="StampField"/> lies inside its area.</summary>
public enum StampFieldAlign
{
    Center,
    Left,
    Right,
}

/// <summary>
/// A text on a stamp picture, e.g. the date of a "Received" stamp. The area is given as a share of the picture (0 to 1,
/// origin top-left), so it doesn't depend on the picture's resolution; the font size follows from the height of the area.
/// </summary>
public sealed record StampField
{
    public double X { get; init; }
    public double Y { get; init; }
    [JsonPropertyName("w")] public double Width { get; init; }
    [JsonPropertyName("h")] public double Height { get; init; }

    /// <summary>One line of text with macros (see <see cref="TemplateExpander"/>), e.g. <c>{$DATE:dd. MMM. yyyy}</c>.</summary>
    public string Text { get; init; } = "";

    /// <summary>Colour as #RRGGBB.</summary>
    public string Color { get; init; } = "#000000";

    [JsonPropertyName("upper")] public bool Uppercase { get; init; }

    /// <summary>
    /// The language dates are written in; it belongs to the stamp ("EINGEGANGEN" stays with a German month in an English
    /// program). Null = the language of the program.
    /// </summary>
    [JsonPropertyName("lang")] public string? Language { get; init; }

    public StampFieldAlign Align { get; init; } = StampFieldAlign.Center;

    /// <summary>The text as it is drawn, with its macros replaced.</summary>
    public string Expand(TemplateContext context)
    {
        var culture = StampCulture.For(Language);
        var text = TemplateExpander.Expand(Text, context, culture);
        return Uppercase ? text.ToUpper(culture) : text;
    }
}

/// <summary>What makes a stamp picture dynamic: the fields written on it each time it is placed.</summary>
public sealed record StampDefinition
{
    public IReadOnlyList<StampField> Fields { get; init; } = [];

    /// <summary>A stamp whose date can be chosen; a stamp with fields but without a date has nothing to ask for.</summary>
    [JsonIgnore]
    public bool UsesDate => Fields.Any(f => TemplateExpander.UsesDate(f.Text));
}

/// <summary>The culture stamp dates are written with.</summary>
internal static class StampCulture
{
    private static readonly string[] German = ["Jan", "Feb", "Mär", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez", ""];
    private static readonly string[] English = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec", ""];

    /// <summary>
    /// The culture of <paramref name="language"/> (null or unknown: the current one) with short month names of its own:
    /// those of the system depend on its ICU version ("Jan", "Jan.", "Sept.", "März"), so a format like "dd. MMM. yyyy"
    /// would look different from one computer to the next, some with two dots in a row.
    /// </summary>
    public static CultureInfo For(string? language)
    {
        var culture = CultureInfo.CurrentCulture;
        if (!string.IsNullOrWhiteSpace(language))
        {
            try
            {
                culture = CultureInfo.GetCultureInfo(language);
            }
            catch (CultureNotFoundException)
            {
            }
        }
        var result = (CultureInfo)culture.Clone();
        var months = result.TwoLetterISOLanguageName switch
        {
            "de" => German,
            "en" => English,
            _ => [.. result.DateTimeFormat.AbbreviatedMonthNames.Select(m => m.TrimEnd('.'))],
        };
        result.DateTimeFormat.AbbreviatedMonthNames = months;
        result.DateTimeFormat.AbbreviatedMonthGenitiveNames = months;
        return result;
    }
}
