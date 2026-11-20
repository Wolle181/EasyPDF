using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Dialogs;

public partial class SplitDialog : Window
{
    public SplitDialog() => InitializeComponent();

    public SplitDialog(SplitDialogContext context) : this() => DataContext = new SplitDialogViewModel(context);

    private SplitDialogViewModel Model => (SplitDialogViewModel)DataContext!;

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("SplitFolder"),
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path != null)
            Model.Folder = path;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Model.BuildRequest());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((SplitRequest?)null);
}
