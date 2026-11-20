using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Dialogs;

public partial class SplitDialogView : DialogView
{
    public SplitDialogView() => InitializeComponent();

    public SplitDialogView(SplitDialogContext context) : this() => DataContext = new SplitDialogViewModel(context);

    private SplitDialogViewModel Model => (SplitDialogViewModel)DataContext!;

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (Host is not { } host)
            return;
        var folders = await host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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

/// <summary>The dialog as a window (desktop).</summary>
public sealed class SplitDialog : DialogWindow
{
    public SplitDialog(SplitDialogContext context) : base(new SplitDialogView(context))
    {
    }
}
