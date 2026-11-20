using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;

namespace EasyPDF.App.Dialogs;

/// <summary>
/// Warning before saving blacked-out content over the original file: save a copy (default), overwrite, or cancel.
/// Closing the window with [X] cancels; "Don't ask again" counts only with Save a copy or Overwrite.
/// </summary>
public partial class RedactedSaveDialog : Window
{
    public RedactedSaveDialog() => InitializeComponent();

    public RedactedSaveDialog(string fileName, int pageCount) : this()
    {
        MessageText.Text = Loc.F("RedactedSaveMessage", fileName);
        DetailsText.Text = Loc.F("RedactedSaveDetails", pageCount);
    }

    private void Answer(RedactedSaveChoice choice) => Close(new RedactedSaveAnswer(choice, DontAskBox.IsChecked == true));

    private void OnCopy(object? sender, RoutedEventArgs e) => Answer(RedactedSaveChoice.SaveCopy);

    private void OnOverwrite(object? sender, RoutedEventArgs e) => Answer(RedactedSaveChoice.Overwrite);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(new RedactedSaveAnswer(RedactedSaveChoice.Cancel, false));
}
