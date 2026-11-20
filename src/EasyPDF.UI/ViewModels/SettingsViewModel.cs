using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Core.Stamps;
using EasyPDF.Core.Templates;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Settings;

namespace EasyPDF.App.ViewModels;

/// <summary>Settings dialog: changes apply immediately and are saved.</summary>
/// <param name="defaultApp">The system's default PDF program; <see cref="SystemDefaultPdfApp"/> if null.</param>
public partial class SettingsViewModel(AppSettings settings, System.Action changed, IDefaultPdfApp? defaultApp = null) : ObservableObject
{
    private readonly IDefaultPdfApp _defaultApp = defaultApp ?? SystemDefaultPdfApp.Instance;

    /// <summary>Whether the program opens PDF files, or what the button did; empty if unknown.</summary>
    [ObservableProperty]
    private string _defaultAppStatus = "";

    /// <summary>Reads again whether the program is the default (also after coming back from the Windows settings).</summary>
    public void RefreshDefaultAppStatus() => DefaultAppStatus = _defaultApp.IsDefault() switch
    {
        true => Loc.T("DefaultAppIsSet"),
        false => Loc.T("DefaultAppIsOther"),
        _ => "",
    };

    /// <summary>"Open PDF files with …": sets the default (Linux, macOS) or opens the Windows settings page for it.</summary>
    [RelayCommand]
    private async Task MakeDefaultAsync() => DefaultAppStatus = await _defaultApp.MakeDefaultAsync() switch
    {
        DefaultAppResult.Done => Loc.T("DefaultAppIsSet"),
        DefaultAppResult.SettingsOpened => Loc.T("DefaultAppSettingsOpened"),
        DefaultAppResult.NotAvailable => Loc.T("DefaultAppNotAvailable"),
        _ => Loc.T("DefaultAppFailed"),
    };

    /// <summary>0 = system, 1 = light, 2 = dark (matches <see cref="ThemeSetting"/>).</summary>
    [ObservableProperty]
    private int _themeIndex = (int)settings.Theme;

    /// <summary>0 = system, 1 = English, 2 = Deutsch.</summary>
    [ObservableProperty]
    private int _languageIndex = settings.Language switch { "en" => 1, "de" => 2, _ => 0 };

    partial void OnThemeIndexChanged(int value)
    {
        settings.Theme = (ThemeSetting)value;
        AppEnvironment.ApplyTheme(settings.Theme);
        changed();
    }

    partial void OnLanguageIndexChanged(int value)
    {
        settings.Language = value switch { 1 => "en", 2 => "de", _ => "" };
        AppEnvironment.ApplyLanguage(settings.Language);
        changed();
    }

    /// <summary>Show the menu bar in the window; the option is offered on Windows and Linux only.</summary>
    [ObservableProperty]
    private bool _showMenuBar = settings.ShowMenuBar;

    public bool CanHideMenuBar { get; } = !System.OperatingSystem.IsMacOS() && !Services.PlatformFeatures.Current.IsWeb; // the web version has no menu bar

    partial void OnShowMenuBarChanged(bool value)
    {
        settings.ShowMenuBar = value;
        changed();
    }

    /// <summary>Ask before blacked-out pages are saved over the original file.</summary>
    [ObservableProperty]
    private bool _warnOverwriteRedacted = settings.WarnOverwriteRedacted;

    partial void OnWarnOverwriteRedactedChanged(bool value)
    {
        settings.WarnOverwriteRedacted = value;
        changed();
    }

    /// <summary>The stamp folder in use (the default while none is chosen).</summary>
    [ObservableProperty]
    private string _stampFolder = StampLibrary.ResolveFolder(settings);

    /// <summary>Sets the stamp folder; null or empty goes back to the default.</summary>
    public void SetStampFolder(string? folder)
    {
        settings.StampFolder = folder ?? "";
        StampFolder = StampLibrary.ResolveFolder(settings);
        changed();
    }

    /// <summary>The templates folder in use (the default while none is chosen).</summary>
    [ObservableProperty]
    private string _templateFolder = TemplateLibrary.ResolveFolder(settings);

    /// <summary>Sets the templates folder; null or empty goes back to the default.</summary>
    public void SetTemplateFolder(string? folder)
    {
        settings.TemplateFolder = folder ?? "";
        TemplateFolder = TemplateLibrary.ResolveFolder(settings);
        changed();
    }
}
