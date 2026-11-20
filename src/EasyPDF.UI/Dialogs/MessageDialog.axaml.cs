using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EasyPDF.App.Dialogs;

public partial class MessageDialogView : DialogView
{
    public MessageDialogView() => InitializeComponent();

    /// <param name="title">Window title; "Error" (from the XAML) if null.</param>
    public MessageDialogView(string message, string? details, string? title = null) : this()
    {
        if (title != null)
            Title = title;
        MessageText.Text = message;
        DetailsText.Text = details;
        DetailsText.IsVisible = !string.IsNullOrEmpty(details);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>The message as a window (desktop).</summary>
public sealed class MessageDialog : DialogWindow
{
    public MessageDialog(string message, string? details, string? title = null) : base(new MessageDialogView(message, details, title))
    {
    }
}
