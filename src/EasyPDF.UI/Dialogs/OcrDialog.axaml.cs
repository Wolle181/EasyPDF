using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Dialogs;

public partial class OcrDialog : Window
{
    public OcrDialog() => InitializeComponent();

    public OcrDialog(OcrDialogContext context) : this() => DataContext = new OcrDialogViewModel(context);

    public OcrDialogViewModel Model => (OcrDialogViewModel)DataContext!;

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Model.BuildRequest());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((OcrRequest?)null);
}
