using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Dialogs;

public partial class RotateDialogView : DialogView
{
    public RotateDialogView() => InitializeComponent();

    public RotateDialogView(RotateDialogContext context) : this() => DataContext = new RotateDialogViewModel(context);

    private void OnOk(object? sender, RoutedEventArgs e) => Close(((RotateDialogViewModel)DataContext!).BuildResult());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((RotateResult?)null);
}

/// <summary>The dialog as a window (desktop).</summary>
public sealed class RotateDialog : DialogWindow
{
    public RotateDialog(RotateDialogContext context) : base(new RotateDialogView(context))
    {
    }
}
