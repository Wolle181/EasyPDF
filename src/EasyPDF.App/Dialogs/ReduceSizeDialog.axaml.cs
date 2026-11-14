using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.Dialogs;

/// <summary>Asks how far the pictures are scaled down ("reduce file size"); closing it with [X] means Cancel (null).</summary>
public partial class ReduceSizeDialog : Window
{
    public ReduceSizeDialog() => InitializeComponent();

    public ReduceSizeDialog(ImageReduction last) : this()
    {
        HighOption.IsChecked = last == ImageReduction.High;
        SmallOption.IsChecked = last == ImageReduction.Small;
        MediumOption.IsChecked = HighOption.IsChecked != true && SmallOption.IsChecked != true;
    }

    public ImageReduction Selected =>
        HighOption.IsChecked == true ? ImageReduction.High : SmallOption.IsChecked == true ? ImageReduction.Small : ImageReduction.Medium;

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Selected);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((ImageReduction?)null);
}
