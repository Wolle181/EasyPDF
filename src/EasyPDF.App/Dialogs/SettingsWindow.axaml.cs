using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Settings;

namespace EasyPDF.App.Dialogs;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(AppSettings settings, System.Action changed, IDefaultPdfApp? defaultApp = null) : this()
    {
        var model = new SettingsViewModel(settings, changed, defaultApp);
        DataContext = model;
        model.RefreshDefaultAppStatus();
        // Back from the Windows settings: show whether the program is the default now
        Activated += (_, _) =>
        {
            if (model.DefaultAppStatus == Localization.Loc.T("DefaultAppSettingsOpened"))
                model.RefreshDefaultAppStatus();
        };
    }

    private SettingsViewModel? Model => DataContext as SettingsViewModel;

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
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(current),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async void OnOpenTemplateFolder(object? sender, RoutedEventArgs e)
    {
        if (Model != null)
            await FolderLauncher.OpenAsync(this, Model.TemplateFolder);
    }

    private void OnResetTemplateFolder(object? sender, RoutedEventArgs e) => Model?.SetTemplateFolder(null);

    private async void OnOpenStampFolder(object? sender, RoutedEventArgs e)
    {
        if (Model != null)
            await FolderLauncher.OpenAsync(this, Model.StampFolder);
    }

    private void OnResetStampFolder(object? sender, RoutedEventArgs e) => Model?.SetStampFolder(null);
}
