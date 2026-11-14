using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EasyPDF.Core.Templates;

/// <summary>A named text snippet that may contain macros like <c>{$DATE}</c> (see <see cref="TemplateExpander"/>).</summary>
public sealed class TextTemplate
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>The file in the templates folder it was read from; null for a new template (see <see cref="TemplateLibrary"/>).</summary>
    public string? FilePath { get; set; }
}

/// <summary>What the macros of a template stand for at the moment a text is placed.</summary>
public sealed record TemplateContext(DateTime Now, string User, string FileName, int Page, int Pages);

/// <summary>
/// Replaces macros in a template: <c>{$DATE}</c> (short date of the current culture), <c>{$DATE:yyyy-MM-dd}</c> (own format),
/// <c>{$TIME}</c>, <c>{$USER}</c>, <c>{$FILE}</c> (name without extension), <c>{$FILENAME}</c> (with extension), <c>{$PAGE}</c>, <c>{$PAGES}</c>.
/// Names are case-insensitive; unknown macros and bad formats stay as typed.
/// </summary>
public static partial class TemplateExpander
{
    [GeneratedRegex(@"\{\$([A-Za-z]+)(?::([^{}]*))?\}")]
    private static partial Regex MacroPattern();

    /// <summary>Whether the template shows a date (<c>{$DATE}</c>, with or without a format).</summary>
    public static bool UsesDate(string template) =>
        MacroPattern().Matches(template).Any(m => m.Groups[1].Value.Equals("DATE", StringComparison.OrdinalIgnoreCase));

    public static string Expand(string template, TemplateContext context, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return MacroPattern().Replace(template, m =>
        {
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;
            try
            {
                switch (m.Groups[1].Value.ToUpperInvariant())
                {
                    case "DATE": return context.Now.ToString(format ?? "d", culture);
                    case "TIME": return context.Now.ToString(format ?? "t", culture);
                    case "USER" when format == null: return context.User;
                    case "FILE" when format == null: return System.IO.Path.GetFileNameWithoutExtension(context.FileName);
                    case "FILENAME" when format == null: return context.FileName;
                    case "PAGE" when format == null: return context.Page.ToString(culture);
                    case "PAGES" when format == null: return context.Pages.ToString(culture);
                }
            }
            catch (FormatException)
            {
            }
            return m.Value;
        });
    }
}
