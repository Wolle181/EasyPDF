using Avalonia.Interactivity;

namespace EasyPDF.App.Dialogs;

/// <summary>A yes/no question; closing it with [X] (or Esc) answers no.</summary>
public partial class QuestionDialogView : DialogView
{
    public QuestionDialogView() => InitializeComponent();

    public QuestionDialogView(string title, string message, string? details = null) : this()
    {
        Title = title;
        MessageText.Text = message;
        DetailsText.Text = details;
        DetailsText.IsVisible = !string.IsNullOrEmpty(details);
    }

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
}

/// <summary>The question as a window (desktop).</summary>
public sealed class QuestionDialog : DialogWindow
{
    public QuestionDialog(string title, string message, string? details = null) : base(new QuestionDialogView(title, message, details))
    {
    }
}
