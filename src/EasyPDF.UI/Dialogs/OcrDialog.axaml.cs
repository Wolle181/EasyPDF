using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Dialogs;

public partial class OcrDialogView : DialogView
{
    public OcrDialogView() => InitializeComponent();

    public OcrDialogView(OcrDialogContext context) : this() => DataContext = new OcrDialogViewModel(context);

    public OcrDialogViewModel Model => (OcrDialogViewModel)DataContext!;

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Model.BuildRequest());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((OcrRequest?)null);
}

/// <summary>The dialog as a window (desktop).</summary>
public sealed class OcrDialog : DialogWindow
{
    public OcrDialog(OcrDialogContext context) : base(new OcrDialogView(context))
    {
    }

    public OcrDialogViewModel Model => ((OcrDialogView)DialogContent).Model;
}
