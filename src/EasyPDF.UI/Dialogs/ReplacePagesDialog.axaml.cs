using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Dialogs;

public partial class ReplacePagesDialog : Window
{
    private bool _accepted;

    public ReplacePagesDialog() => InitializeComponent();

    public ReplacePagesDialog(ReplacePagesContext context) : this()
    {
        DataContext = new ReplacePagesDialogViewModel(context);
        // A chosen file that is not used (Cancel, [X], another choice) is closed again
        Closed += async (_, _) =>
        {
            if (!_accepted)
                await Model.DisposeAsync();
        };
    }

    public ReplacePagesDialogViewModel Model => (ReplacePagesDialogViewModel)DataContext!;

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("ReplacePickTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.T("FilterPdf")) { Patterns = ["*.pdf"], AppleUniformTypeIdentifiers =
                    ["com.adobe.pdf"], MimeTypes = ["application/pdf"]
                }
            ],
        });
        if (files.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault() is { } path)
            await Model.SetFileAsync(path);
    }

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        var result = await Model.BuildResultAsync();
        if (result == null)
            return;
        _accepted = true;
        await Model.DisposeAsync();   // nothing left to close: the result owns the document
        Close(result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((ReplacePagesResult?)null);
}
