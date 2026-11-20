using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.App.Dialogs;

/// <summary>
/// The files embedded in the PDF: name, size, e-invoice marked. Open (only harmless types, also by double-click),
/// Save, or Close; closing the window with [X] is Close (no choice).
/// </summary>
public partial class AttachmentsDialog : Window
{
    private readonly IReadOnlyList<PdfAttachment> _attachments = [];

    public AttachmentsDialog() => InitializeComponent();

    public AttachmentsDialog(IReadOnlyList<PdfAttachment> attachments) : this()
    {
        _attachments = attachments;
        AttachmentList.ItemsSource = attachments.Select(Describe).ToList();
        AttachmentList.SelectionChanged += (_, _) => UpdateButtons();
        // The e-invoice is what people look for; otherwise the first file
        AttachmentList.SelectedIndex = attachments.Count == 0 ? -1 : System.Math.Max(0, attachments.ToList().FindIndex(a => a.IsEInvoice));
        UpdateButtons();
    }

    /// <summary>The selected attachment; null if none.</summary>
    public PdfAttachment? Selected => AttachmentList.SelectedIndex is var i && i >= 0 && i < _attachments.Count ? _attachments[i] : null;

    public static string Describe(PdfAttachment attachment)
    {
        var text = Loc.F("AttachmentItem", attachment.Name, FormatSize(attachment.Size));
        return attachment.IsEInvoice ? Loc.F("AttachmentItemEInvoice", text) : text;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => Loc.F("SizeBytes", bytes),
        < 1024 * 1024 => Loc.F("SizeKilobytes", (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)),
        _ => Loc.F("SizeMegabytes", (bytes / 1024.0 / 1024.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)),
    };

    private void UpdateButtons()
    {
        var selected = Selected;
        OpenButton.IsEnabled = selected is { CanOpen: true };
        SaveButton.IsEnabled = selected != null;
        CannotOpenText.IsVisible = selected is { CanOpen: false };
    }

    private void Answer(AttachmentAction action)
    {
        if (Selected is { } attachment && (action != AttachmentAction.Open || attachment.CanOpen))
            Close(new AttachmentChoice(attachment, action));
    }

    private void OnOpen(object? sender, RoutedEventArgs e) => Answer(AttachmentAction.Open);

    private void OnSave(object? sender, RoutedEventArgs e) => Answer(AttachmentAction.Save);

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => Answer(AttachmentAction.Open);
}
