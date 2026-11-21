using System.Collections.Generic;
using System.Windows.Input;
using EasyPDF.App.ViewModels;

namespace EasyPDF.Browser;

/// <summary>
/// The commands the web version offers so far; the others (stamps, texts, redaction, OCR, printing, mail, settings…)
/// are hidden until they work in the browser too.
/// </summary>
internal static class WebCommands
{
    public static bool IsAvailable(MainViewModel vm, ICommand command) =>
        command == vm.OpenCommand || command == vm.SaveCommand || command == vm.SaveAsCommand
        || command == vm.ConcatenateCommand || command == vm.ExtractPagesCommand || command == vm.SplitDocumentCommand
        || command == vm.ReduceSizeCommand
        || command == vm.InsertBeforeCommand || command == vm.InsertAfterCommand
        || command == vm.InsertBlankBeforeCommand || command == vm.InsertBlankAfterCommand
        || command == vm.DeletePagesCommand || command == vm.RotatePagesCommand || command == vm.ReplacePagesCommand
        || command == vm.RotateLeftCommand || command == vm.RotateRightCommand
        || command == vm.UndoCommand || command == vm.RedoCommand
        || command == vm.ShowReadingCommand || command == vm.ShowOrganizeCommand || command == vm.SwitchModeCommand
        || command == vm.TogglePageSidebarCommand || command == vm.ToggleRibbonCommand
        || command == vm.GoToFirstPageCommand || command == vm.GoToLastPageCommand || command == vm.ShowGoToPageCommand
        || command == vm.GoToTypedPageCommand
        || command == vm.FitHeightCommand || command == vm.FitWidthCommand || command == vm.FitBestCommand
        || command == vm.ZoomInCommand || command == vm.ZoomOutCommand || command == vm.ActualSizeCommand
        || command == vm.ApplyZoomInputCommand
        || command == vm.SelectAllCommand || command == vm.CloseDocumentCommand
        || command == vm.ShowSettingsCommand || command == vm.ShowAboutCommand || command == vm.ShowQuickGuideCommand
        || command == vm.ShowIntroVideoCommand
        // Attachments and e-invoice (saved as download; a browser cannot open them in a program of the system)
        || command == vm.ShowAttachmentsCommand || command == vm.OpenEInvoiceCommand || command == vm.SaveEInvoiceCommand
        || command == vm.DismissEInvoiceBarCommand
        // Text: the server reads the words of the text layer
        || command == vm.ToggleSelectTextToolCommand || command == vm.CopyTextCommand
        || command == vm.ShowSearchCommand || command == vm.FindNextCommand || command == vm.FindPreviousCommand
        || command == vm.CloseSearchCommand
        || command == vm.CancelOperationCommand || command == vm.CancelStampCommand
        // Black out / white out, texts, copying an area (the server writes them with the page list)
        || command == vm.RedactCommand || command == vm.CoverCommand || command == vm.SelectTextToolCommand
        || command == vm.ToggleCopyAreaToolCommand || command == vm.PasteAreaCommand
        // Text recognition on the server
        || command == vm.RecognizeTextCommand
        // Printing: a new tab with the PDF, printed by the browser's viewer
        || command == vm.PrintCommand;

    /// <summary>Ribbon buttons without a command (a gallery or menu) the web version offers.</summary>
    public static IReadOnlySet<string> Buttons { get; } = new HashSet<string> { "TextStyleButton" };
}
