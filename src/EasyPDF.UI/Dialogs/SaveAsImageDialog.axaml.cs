using Avalonia.Interactivity;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.Dialogs;

/// <summary>Asks for the resolution and colour of "Save as image"; closing it with [X] means Cancel (null).</summary>
public partial class SaveAsImageDialogView : DialogView
{
    public SaveAsImageDialogView() => InitializeComponent();

    public SaveAsImageDialogView(PageRasterization last) : this()
    {
        Dpi150Option.IsChecked = last.Dpi == 150;
        Dpi300Option.IsChecked = last.Dpi == 300;
        Dpi200Option.IsChecked = Dpi150Option.IsChecked != true && Dpi300Option.IsChecked != true;
        GrayscaleBox.IsChecked = last.Grayscale;
    }

    public PageRasterization Selected => new(
        Dpi150Option.IsChecked == true ? 150 : Dpi300Option.IsChecked == true ? 300 : 200,
        GrayscaleBox.IsChecked == true);

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Selected);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((PageRasterization?)null);
}

/// <summary>The dialog as a window (desktop).</summary>
public sealed class SaveAsImageDialog : DialogWindow
{
    public SaveAsImageDialog(PageRasterization last) : base(new SaveAsImageDialogView(last))
    {
    }

    public PageRasterization Selected => ((SaveAsImageDialogView)DialogContent).Selected;
}
