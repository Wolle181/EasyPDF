using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;

namespace EasyPDF.App.Dialogs;

public partial class UnsavedChangesDialogView : DialogView
{
    public UnsavedChangesDialogView() => InitializeComponent();

    public UnsavedChangesDialogView(string fileName) : this() => MessageText.Text = Loc.F("UnsavedMessage", fileName);

    private void OnSave(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Save);

    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Discard);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Cancel);
}

/// <summary>The question as a window (desktop); closing it with [X] is <see cref="UnsavedChoice.Cancel"/>.</summary>
public sealed class UnsavedChangesDialog : DialogWindow
{
    public UnsavedChangesDialog(string fileName) : base(new UnsavedChangesDialogView(fileName))
    {
    }
}
