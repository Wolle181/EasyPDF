using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using EasyPDF.App.Localization;
using EasyPDF.Core.Settings;

namespace EasyPDF.App.Services;

/// <summary>Applies the user's theme and language settings to the running application.</summary>
public static class AppEnvironment
{
    public static readonly string[] SupportedLanguages = ["en", "de"];

    public static void Apply(AppSettings settings)
    {
        ApplyTheme(settings.Theme);
        ApplyLanguage(settings.Language);
    }

    public static void ApplyTheme(ThemeSetting theme)
    {
        if (Application.Current == null)
            return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            ThemeSetting.Light => ThemeVariant.Light,
            ThemeSetting.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>"" = follow the OS (German if the OS UI language is German, otherwise English).</summary>
    public static void ApplyLanguage(string language) => Loc.SetCulture(ResolveCulture(language));

    public static CultureInfo ResolveCulture(string language)
    {
        if (!string.IsNullOrEmpty(language))
            return new CultureInfo(language);

        return CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "de" ? new CultureInfo("de") : new CultureInfo("en");
    }
}
