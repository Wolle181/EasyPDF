using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EasyPDF.App.Dialogs;

/// <summary>A yes/no question; closing the window with [X] answers no.</summary>
public partial class QuestionDialog : Window
{
    public QuestionDialog() => InitializeComponent();

    public QuestionDialog(string title, string message, string? details = null) : this()
    {
        Title = title;
        MessageText.Text = message;
        DetailsText.Text = details;
        DetailsText.IsVisible = !string.IsNullOrEmpty(details);
    }

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
}
