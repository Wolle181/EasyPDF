using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;

namespace EasyPDF.App.Dialogs;

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog() => InitializeComponent();

    public UnsavedChangesDialog(string fileName) : this() => MessageText.Text = Loc.F("UnsavedMessage", fileName);

    private void OnSave(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Save);

    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Discard);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Cancel);
}
