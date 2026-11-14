using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.Services;

public enum UnsavedChoice
{
    /// <summary>First on purpose: closing the dialog window with [X] means Cancel.</summary>
    Cancel,
    Save,
    Discard,
}

/// <summary>Answer to the warning before saving blacked-out content over the original file.</summary>
public enum RedactedSaveChoice
{
    /// <summary>First on purpose: closing the dialog window with [X] means Cancel.</summary>
    Cancel,
    Overwrite,
    SaveCopy,
}

/// <param name="DontAskAgain">"Don't ask again" was ticked (switches the warning off in the settings).</param>
public sealed record RedactedSaveAnswer(RedactedSaveChoice Choice, bool DontAskAgain);

/// <param name="PageCount">Pages in the document.</param>
/// <param name="CurrentPage">Zero-based "current" page (reading mode: page at the left edge, Organize: focused thumbnail).</param>
/// <param name="SelectedPages">Zero-based selected pages (Organize mode); enables the "selected pages" option.</param>
/// <param name="LastDegrees">Direction used last time in this session (90, -90 or 180).</param>
public sealed record RotateDialogContext(int PageCount, int CurrentPage, IReadOnlyList<int> SelectedPages, int LastDegrees)
{
    public bool HasSelection => SelectedPages is { Count: > 0 };
}

/// <param name="PageCount">Pages in the document.</param>
/// <param name="SelectedPages">Zero-based selected pages; each starts a new part in "at selected pages" mode.</param>
/// <param name="DefaultFolder">Initial output folder.</param>
/// <param name="BaseName">File name without extension; parts become "&lt;BaseName&gt;_part01.pdf" …</param>
public sealed record SplitDialogContext(int PageCount, IReadOnlyList<int> SelectedPages, string DefaultFolder, string BaseName);

public sealed record SplitRequest(IReadOnlyList<EasyPDF.Core.Documents.SplitPart> Parts, string Folder, string BaseName);

/// <param name="HasCurrentDocument">Whether the open document can be included as an entry.</param>
/// <param name="InitialFiles">Files listed right away, in this order (e.g. several PDFs given on the command line).</param>
/// <param name="OtherDocuments">The documents of the other windows at the moment of asking; null without several windows (tests).</param>
public sealed record ConcatenateContext(bool HasCurrentDocument, IReadOnlyList<string>? InitialFiles = null,
    Func<IReadOnlyList<OpenDocumentInfo>>? OtherDocuments = null);

/// <param name="Name">Shown name (as in the window title).</param>
/// <param name="Document">The window's document; not owned, it is copied when added to the list.</param>
public sealed record OpenDocumentInfo(string Name, EditableDocument Document);

/// <param name="Parts">Entries in order: a document per file or open window; <c>null</c> stands for the current document. The caller owns the documents.</param>
public sealed record ConcatenateResult(IReadOnlyList<EditableDocument?> Parts);

/// <param name="FileName">Name of the file whose pages are inserted.</param>
/// <param name="PageCount">Pages in that file.</param>
public sealed record InsertRangeContext(string FileName, int PageCount);

/// <param name="PageCount">Pages in the document.</param>
/// <param name="CurrentPage">Zero-based current (or right-clicked) page.</param>
/// <param name="SelectedPages">Zero-based selected pages (Organize mode); enables and preselects "selected pages".</param>
/// <param name="OtherDocuments">The documents of the other windows; null without several windows (tests).</param>
public sealed record ReplacePagesContext(int PageCount, int CurrentPage, IReadOnlyList<int> SelectedPages,
    Func<IReadOnlyList<OpenDocumentInfo>>? OtherDocuments = null)
{
    public bool HasSelection => SelectedPages is { Count: > 0 };
}

/// <param name="First">Zero-based first page to replace.</param>
/// <param name="Count">Number of consecutive pages to replace.</param>
/// <param name="Replacement">The document the new pages come from (a file or the copy of another window's document); the caller owns it.</param>
/// <param name="Pages">Zero-based pages of <paramref name="Replacement"/>, in this order.</param>
public sealed record ReplacePagesResult(int First, int Count, EditableDocument Replacement, IReadOnlyList<int> Pages);

/// <param name="Degrees">90, -90 or 180.</param>
/// <param name="Pages">Zero-based page indices.</param>
public sealed record RotateResult(int Degrees, IReadOnlyList<int> Pages);

/// <param name="PageCount">Pages in the document.</param>
/// <param name="CurrentPage">Zero-based current page.</param>
/// <param name="SelectedPages">Zero-based selected pages (Organize mode); enables and preselects "selected pages".</param>
public sealed record OcrDialogContext(int PageCount, int CurrentPage, IReadOnlyList<int> SelectedPages)
{
    public bool HasSelection => SelectedPages is { Count: > 0 };
}

/// <param name="Pages">Zero-based pages to recognize.</param>
/// <param name="SkipPagesWithText">Leave out pages that already have text (no second text layer).</param>
public sealed record OcrRequest(IReadOnlyList<int> Pages, bool SkipPagesWithText);

/// <summary>UI interactions the view models need; implemented by the main window.</summary>
public interface IDialogService
{
    /// <summary>Shows the native open dialog; returns the chosen path or null.</summary>
    Task<string?> PickPdfFileAsync();

    /// <summary>Open dialog allowing several files; empty if cancelled.</summary>
    Task<IReadOnlyList<string>> PickPdfFilesAsync(string title);

    /// <summary>Native save dialog; returns the chosen path or null.</summary>
    Task<string?> PickSavePathAsync(string suggestedFileName, string? initialFolder);

    Task<RotateResult?> ShowRotateDialogAsync(RotateDialogContext context);

    /// <summary>Asks which pages of a single file to insert; returns zero-based pages, or null if cancelled.</summary>
    Task<IReadOnlyList<int>?> ShowInsertRangeDialogAsync(InsertRangeContext context);

    /// <summary>Which consecutive pages to replace and by what; null if cancelled.</summary>
    Task<ReplacePagesResult?> ShowReplacePagesDialogAsync(ReplacePagesContext context);

    Task<SplitRequest?> ShowSplitDialogAsync(SplitDialogContext context);

    /// <summary>Which pages to recognize (OCR); null if cancelled.</summary>
    Task<OcrRequest?> ShowOcrDialogAsync(OcrDialogContext context);

    Task<ConcatenateResult?> ShowConcatenateDialogAsync(ConcatenateContext context);

    /// <summary>How far to scale down the pictures (<paramref name="last"/> is preselected); null if cancelled.</summary>
    Task<ImageReduction?> ShowReduceSizeDialogAsync(ImageReduction last);

    Task<UnsavedChoice> AskUnsavedChangesAsync(string fileName);

    /// <summary>Warns before saving over the original with blacked-out pages; closing the dialog means Cancel.</summary>
    Task<RedactedSaveAnswer> AskOverwriteRedactedAsync(string fileName, int pageCount);

    /// <summary>Printer, copies and page range; null if cancelled.</summary>
    Task<PrintRequest?> ShowPrintDialogAsync(PrintDialogContext context);

    Task ShowAboutAsync();

    /// <summary>The quick guide: a short tour of all functions (not modal).</summary>
    Task ShowQuickGuideAsync();

    /// <summary>Plays the introduction video in the system's video player; tells the user if there is none.</summary>
    Task ShowIntroVideoAsync();

    Task ShowSettingsAsync();

    Task ShowErrorAsync(string message, string? details = null);

    /// <summary>A message that is not an error (e.g. the result of an operation).</summary>
    Task ShowInfoAsync(string message, string? details = null);

    /// <summary>Puts plain text on the system clipboard.</summary>
    Task CopyToClipboardAsync(string text);

    void CloseWindow();
}
