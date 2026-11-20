using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EasyPDF.App.Dialogs;

public partial class MessageDialog : Window
{
    public MessageDialog() => InitializeComponent();

    /// <param name="title">Window title; "Error" (from the XAML) if null.</param>
    public MessageDialog(string message, string? details, string? title = null) : this()
    {
        if (title != null)
            Title = title;
        MessageText.Text = message;
        DetailsText.Text = details;
        DetailsText.IsVisible = !string.IsNullOrEmpty(details);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
