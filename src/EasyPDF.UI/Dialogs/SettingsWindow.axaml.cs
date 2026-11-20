using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Settings;

namespace EasyPDF.App.Dialogs;

public partial class SettingsView : DialogView
{
    public SettingsView() => InitializeComponent();

    public SettingsView(AppSettings settings, System.Action changed, IDefaultPdfApp? defaultApp = null) : this()
    {
        var model = new SettingsViewModel(settings, changed, defaultApp);
        DataContext = model;
        // The web version has no stamp/template folders, no redaction and no default PDF program (yet): theme and language
        if (PlatformFeatures.Current.IsWeb)
        {
            StampSection.IsVisible = TemplateSection.IsVisible = RedactSection.IsVisible = PdfFilesSection.IsVisible = false;
            return;
        }
        model.RefreshDefaultAppStatus();
    }

    private SettingsViewModel? Model => DataContext as SettingsViewModel;

    /// <summary>Back from the Windows settings: shows whether the program is the default now.</summary>
    public void RefreshAfterSystemSettings()
    {
        if (Model is { } model && model.DefaultAppStatus == Localization.Loc.T("DefaultAppSettingsOpened"))
            model.RefreshDefaultAppStatus();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnChooseStampFolder(object? sender, RoutedEventArgs e)
    {
        if (Model != null && await PickFolderAsync(Model.StampFolder) is { } path)
            Model.SetStampFolder(path);
    }

    private async void OnChooseTemplateFolder(object? sender, RoutedEventArgs e)
    {
        if (Model != null && await PickFolderAsync(Model.TemplateFolder) is { } path)
            Model.SetTemplateFolder(path);
    }

    /// <summary>Lets the user choose a folder, starting in <paramref name="current"/>; null if cancelled.</summary>
    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string current)
    {
        if (Host is not { } host)
            return null;
        var folders = await host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            SuggestedStartLocation = await host.StorageProvider.TryGetFolderFromPathAsync(current),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async void OnOpenTemplateFolder(object? sender, RoutedEventArgs e)
    {
        if (Model != null && Host is Window window)
            await FolderLauncher.OpenAsync(window, Model.TemplateFolder);
    }

    private void OnResetTemplateFolder(object? sender, RoutedEventArgs e) => Model?.SetTemplateFolder(null);

    private async void OnOpenStampFolder(object? sender, RoutedEventArgs e)
    {
        if (Model != null && Host is Window window)
            await FolderLauncher.OpenAsync(window, Model.StampFolder);
    }

    private void OnResetStampFolder(object? sender, RoutedEventArgs e) => Model?.SetStampFolder(null);
}

/// <summary>The settings as a window (desktop).</summary>
public sealed class SettingsWindow : DialogWindow
{
    public SettingsWindow(AppSettings settings, System.Action changed, IDefaultPdfApp? defaultApp = null)
        : base(new SettingsView(settings, changed, defaultApp)) =>
        Activated += (_, _) => ((SettingsView)DialogContent).RefreshAfterSystemSettings();
}
