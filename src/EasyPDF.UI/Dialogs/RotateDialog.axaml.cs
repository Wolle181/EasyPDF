using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Dialogs;

public partial class RotateDialog : Window
{
    public RotateDialog() => InitializeComponent();

    public RotateDialog(RotateDialogContext context) : this() => DataContext = new RotateDialogViewModel(context);

    private void OnOk(object? sender, RoutedEventArgs e) => Close(((RotateDialogViewModel)DataContext!).BuildResult());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((RotateResult?)null);
}
