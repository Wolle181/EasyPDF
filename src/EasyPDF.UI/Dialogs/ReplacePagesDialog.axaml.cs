using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Dialogs;

public partial class ReplacePagesDialogView : DialogView
{
    private bool _accepted;

    public ReplacePagesDialogView() => InitializeComponent();

    public ReplacePagesDialogView(ReplacePagesContext context) : this()
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
        if (Host is not { } host)
            return;
        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        // A local path, or an upload in the web version
        if (files.Count > 0 && await PlatformFeatures.Files.ToOpenPathAsync(files[0]) is { } path)
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

/// <summary>The dialog as a window (desktop).</summary>
public sealed class ReplacePagesDialog : DialogWindow
{
    public ReplacePagesDialog(ReplacePagesContext context) : base(new ReplacePagesDialogView(context))
    {
    }

    public ReplacePagesDialogViewModel Model => ((ReplacePagesDialogView)DialogContent).Model;
}
