using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.App.ViewModels;

/// <summary>
/// Files embedded in the PDF. The Attachments button lists them; each one can be saved or, if it is a harmless type
/// (<see cref="PdfAttachment.CanOpen"/>), opened in the system's program. An electronic invoice (ZUGFeRD, Factur-X,
/// XRechnung) is announced by a bar above the pages with Open and Save. The attachments are those of the document's file
/// (<see cref="EditableDocument.BaseSource"/>); saving keeps them.
/// </summary>
public partial class MainViewModel
{
    private PdfSource? _attachmentSource;

    /// <summary>The embedded files of the document's file (a new list on every change, so bindings update).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachments), nameof(EInvoice), nameof(EInvoiceBarVisible))]
    private IReadOnlyList<PdfAttachment> _attachments = [];

    /// <summary>The e-invoice bar was closed for this document.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EInvoiceBarVisible))]
    private bool _isEInvoiceBarDismissed;

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>The XML of the electronic invoice among the attachments; null if there is none.</summary>
    public PdfAttachment? EInvoice => Attachments.FirstOrDefault(a => a.IsEInvoice);

    public bool EInvoiceBarVisible => EInvoice != null && !IsEInvoiceBarDismissed;

    /// <summary>The reading of the attachments started last (tests await it).</summary>
    public Task AttachmentsLoading { get; private set; } = Task.CompletedTask;

    partial void OnAttachmentsChanged(IReadOnlyList<PdfAttachment> value) => RefreshAttachmentCommands();

    private void RefreshAttachmentCommands()
    {
        ShowAttachmentsCommand.NotifyCanExecuteChanged();
        OpenEInvoiceCommand.NotifyCanExecuteChanged();
        SaveEInvoiceCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the attachments again when the document's file changed (other document, or reopened after saving).</summary>
    private void RefreshAttachments()
    {
        var source = Document?.BaseSource;
        if (source == _attachmentSource)
            return;
        _attachmentSource = source;
        Attachments = [];
        if (source != null)
            AttachmentsLoading = LoadAttachmentsAsync(source);
    }

    private async Task LoadAttachmentsAsync(PdfSource source)
    {
        IReadOnlyList<PdfAttachment> list;
        try
        {
            list = await source.ListAttachmentsAsync();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or IOException)
        {
            return;
        }
        if (source == _attachmentSource) // still the document's file
            Attachments = list;
    }

    private bool CanUseAttachments() => CanUseDocument() && HasAttachments;

    private bool CanUseEInvoice() => CanUseDocument() && EInvoice != null;

    /// <summary>Lists the attachments; the chosen one is opened or saved.</summary>
    [RelayCommand(CanExecute = nameof(CanUseAttachments))]
    private async Task ShowAttachmentsAsync()
    {
        if (await Dialogs.ShowAttachmentsAsync(Attachments) is not { } choice)
            return;
        if (choice.Action == AttachmentAction.Open)
            await OpenAttachmentAsync(choice.Attachment);
        else
            await SaveAttachmentAsync(choice.Attachment);
    }

    [RelayCommand(CanExecute = nameof(CanUseEInvoice))]
    private Task OpenEInvoiceAsync() => EInvoice is { } invoice ? OpenAttachmentAsync(invoice) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanUseEInvoice))]
    private Task SaveEInvoiceAsync() => EInvoice is { } invoice ? SaveAttachmentAsync(invoice) : Task.CompletedTask;

    [RelayCommand]
    private void DismissEInvoiceBar() => IsEInvoiceBarDismissed = true;

    /// <summary>Writes the attachment to a temporary folder and opens it in the system's program (only harmless types).</summary>
    private async Task OpenAttachmentAsync(PdfAttachment attachment)
    {
        if (!attachment.CanOpen || _attachmentSource is not { } source)
            return;
        string path;
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), AppInfo.Name + "-attachments", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, attachment.SafeFileName);
            await File.WriteAllBytesAsync(path, await source.ReadAttachmentAsync(attachment.Index));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(Loc.F("ErrAttachmentRead", attachment.Name), ex.Message);
            return;
        }
        if (!await Dialogs.OpenInSystemAsync(path))
            await Dialogs.ShowErrorAsync(Loc.F("ErrAttachmentOpen", attachment.SafeFileName), path);
    }

    /// <summary>Asks where to save the attachment and writes it there; an e-invoice is suggested under the PDF's name.</summary>
    private async Task SaveAttachmentAsync(PdfAttachment attachment)
    {
        if (_attachmentSource is not { } source || Document is not { } document)
            return;
        var suggested = attachment.IsEInvoice ? BaseNameOf(document) + ".xml" : attachment.SafeFileName;
        if (await Dialogs.PickSaveFilePathAsync(suggested, FolderOf(document)) is not { } path)
            return;
        try
        {
            await File.WriteAllBytesAsync(path, await source.ReadAttachmentAsync(attachment.Index));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowErrorAsync(Loc.F("ErrAttachmentSave", attachment.Name), ex.Message);
            return;
        }
        ShowMessage(Loc.F("StatusAttachmentSaved", Path.GetFileName(path)));
    }
}
