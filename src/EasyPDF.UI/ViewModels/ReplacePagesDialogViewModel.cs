using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.App.ViewModels;

/// <summary>Which pages of the open document are replaced.</summary>
public enum ReplaceTarget
{
    Current,
    Selected,
    Range,
}

/// <summary>
/// An entry of the "open document" list: another window's document (<see cref="Info"/>), or in the web version another
/// browser tab's (<see cref="Tab"/>, copied from that tab on OK).
/// </summary>
public sealed class ReplaceDocumentItem
{
    public ReplaceDocumentItem(OpenDocumentInfo info) => Info = info;

    public ReplaceDocumentItem(TabDocumentInfo tab) => Tab = tab;

    public OpenDocumentInfo? Info { get; }

    public TabDocumentInfo? Tab { get; }

    public string Name => Info?.Name ?? Tab!.Name;

    /// <summary>A window's document as it is now; a tab's as the tab described it when asked.</summary>
    public int PageCount => Info?.Document.PageCount ?? Tab!.PageCount;

    public string Label => Loc.F("ReplaceDocumentEntry", Name, Loc.Pages(PageCount));
}

/// <summary>
/// The "Replace pages" dialog: consecutive pages of the open document are removed and pages of another open document or a
/// file take their place. A chosen file is opened right away (page count, errors) and closed again unless it is used.
/// </summary>
public partial class ReplacePagesDialogViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ReplacePagesContext _context;
    private EditableDocument? _file;
    private readonly ObservableCollection<ReplaceDocumentItem> _openDocuments;

    public ReplacePagesDialogViewModel(ReplacePagesContext context)
    {
        _context = context;
        _target = context.SelectedPages.Count > 1 ? ReplaceTarget.Selected : ReplaceTarget.Current;
        _openDocuments = [.. (context.OtherDocuments?.Invoke() ?? []).Select(d => new ReplaceDocumentItem(d))];
        _selectedOpenDocument = _openDocuments.FirstOrDefault();
        _useOpenDocument = HasOpenDocuments;
    }

    /// <summary>
    /// Web version: adds the documents of the other browser tabs to the list (when the dialog is shown); the first one is
    /// chosen unless the user already chose something.
    /// </summary>
    public async Task FindOtherTabsAsync()
    {
        if (_context.OtherTabs == null)
            return;
        var found = await _context.OtherTabs.OtherDocumentsAsync();
        if (found.Count == 0)
            return;
        foreach (var tab in found)
            _openDocuments.Add(new ReplaceDocumentItem(tab));
        OnPropertyChanged(nameof(HasOpenDocuments));
        if (SelectedOpenDocument == null && FilePath == null)
            SelectedOpenDocument = _openDocuments[0];   // also selects "open document"
    }

    #region Pages to replace

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrent), nameof(IsSelected), nameof(IsTargetRange))]
    private ReplaceTarget _target;

    [ObservableProperty]
    private string _targetRangeText = "";

    public bool CanSelectSelected => _context.HasSelection;

    public string CurrentPageText => Loc.F("RotateScopeCurrent", _context.CurrentPage + 1);

    public bool IsCurrent { get => Target == ReplaceTarget.Current; set { if (value) Target = ReplaceTarget.Current; } }

    public bool IsSelected { get => Target == ReplaceTarget.Selected; set { if (value) Target = ReplaceTarget.Selected; } }

    public bool IsTargetRange { get => Target == ReplaceTarget.Range; set { if (value) Target = ReplaceTarget.Range; } }

    partial void OnTargetChanged(ReplaceTarget value) => Revalidate();

    partial void OnTargetRangeTextChanged(string value) => Revalidate();

    /// <summary>The zero-based pages to replace, or null with an explanation in <paramref name="error"/>.</summary>
    private IReadOnlyList<int>? TargetPages(out string error)
    {
        error = "";
        IReadOnlyList<int> pages;
        switch (Target)
        {
            case ReplaceTarget.Selected:
                pages = [.. _context.SelectedPages.Distinct().Order()];
                break;
            case ReplaceTarget.Range:
                var range = PageRangeParser.Parse(TargetRangeText, _context.PageCount);
                if (!range.Success)
                {
                    error = RangeErrors.Text(range);
                    return null;
                }
                pages = [.. range.Pages.Distinct().Order()];
                break;
            default:
                return [_context.CurrentPage];
        }
        if (pages.Count == 0 || pages[^1] - pages[0] != pages.Count - 1)
        {
            error = Loc.T("ErrReplaceNotConsecutive");
            return null;
        }
        return pages;
    }

    public string TargetErrorText
    {
        get
        {
            TargetPages(out var error);
            return error;
        }
    }

    #endregion

    #region Replacement

    public IReadOnlyList<ReplaceDocumentItem> OpenDocuments => _openDocuments;

    /// <summary>Whether other windows (web version: browser tabs) have a document; otherwise only a file can be chosen.</summary>
    public bool HasOpenDocuments => _openDocuments.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseFile))]
    private bool _useOpenDocument;

    public bool UseFile { get => !UseOpenDocument; set => UseOpenDocument = !value; }

    [ObservableProperty]
    private ReplaceDocumentItem? _selectedOpenDocument;

    partial void OnUseOpenDocumentChanged(bool value) => Revalidate();

    partial void OnSelectedOpenDocumentChanged(ReplaceDocumentItem? value)
    {
        _copyError = "";
        if (value != null)
            UseOpenDocument = true;
        Revalidate();
    }

    /// <summary>Path of the chosen file (opened successfully or not); null before one is chosen.</summary>
    [ObservableProperty]
    private string? _filePath;

    private string _fileError = "";
    private string _copyError = "";

    /// <summary>Why the chosen file (not a PDF, password) or open document (its file is gone) cannot be used; empty if fine.</summary>
    public string SourceErrorText => UseOpenDocument ? _copyError : _fileError;

    public string FileText => FilePath == null
        ? Loc.T("ReplaceNoFile")
        : _file != null ? Loc.F("ReplaceDocumentEntry", Path.GetFileName(FilePath), Loc.Pages(_file.PageCount)) : Path.GetFileName(FilePath);

    /// <summary>Opens <paramref name="path"/> as the replacement and selects "file"; the previous file is closed.</summary>
    public async Task SetFileAsync(string path)
    {
        var previous = _file;
        _file = null;
        _fileError = "";
        try
        {
            var source = await PdfBackend.Current.OpenAsync(path);
            _file = EditableDocument.FromSource(source);
        }
        catch (PdfOpenException ex)
        {
            _fileError = Loc.T(ex.IsPasswordProtected ? "ErrPasswordProtected" : "ErrNotAPdf");
        }
        FilePath = path;
        UseFile = true;
        if (previous != null)
            await previous.DisposeAsync();
        OnPropertyChanged(nameof(FileText));
        Revalidate();
    }

    /// <summary>The pages of the document the new pages come from (not yet copied for an open document); null if none is usable.</summary>
    private int? ReplacementPageCount => UseOpenDocument ? SelectedOpenDocument?.PageCount : _file?.PageCount;

    private string ReplacementName => UseOpenDocument ? SelectedOpenDocument?.Name ?? "" : Path.GetFileName(FilePath ?? "");

    #endregion

    #region Pages to insert

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsertRange))]
    private bool _insertAllPages = true;

    public bool IsInsertRange { get => !InsertAllPages; set => InsertAllPages = !value; }

    [ObservableProperty]
    private string _insertRangeText = "";

    partial void OnInsertAllPagesChanged(bool value) => Revalidate();

    partial void OnInsertRangeTextChanged(string value) => Revalidate();

    /// <summary>The zero-based pages of the replacement to insert, or null with an explanation in <paramref name="error"/>.</summary>
    private IReadOnlyList<int>? InsertPages(out string error)
    {
        error = "";
        if (ReplacementPageCount is not { } pageCount)
            return null;
        if (InsertAllPages)
            return [.. Enumerable.Range(0, pageCount)];
        var range = PageRangeParser.Parse(InsertRangeText, pageCount);
        if (!range.Success)
        {
            error = RangeErrors.Text(range);
            return null;
        }
        return range.Pages;
    }

    public string InsertErrorText
    {
        get
        {
            InsertPages(out var error);
            return error;
        }
    }

    #endregion

    public bool IsValid => TargetPages(out _) != null && InsertPages(out _) != null;

    /// <summary>What OK will do, or what is still missing.</summary>
    public string SummaryText
    {
        get
        {
            var target = TargetPages(out _);
            var insert = InsertPages(out _);
            if (ReplacementPageCount == null)
                return Loc.T("ReplaceChooseSource");
            if (target == null || insert == null)
                return "";
            return Loc.F("ReplaceSummary", target.Count, insert.Count, ReplacementName);
        }
    }

    private void Revalidate()
    {
        OnPropertyChanged(nameof(SourceErrorText));
        OnPropertyChanged(nameof(TargetErrorText));
        OnPropertyChanged(nameof(InsertErrorText));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>
    /// The result for OK; the caller owns its document. Another window's document is copied now, with its unsaved changes,
    /// so the result does not depend on that window. Null if the input is invalid or the copy fails (reason in
    /// <see cref="SourceErrorText"/>).
    /// </summary>
    public async Task<ReplacePagesResult?> BuildResultAsync()
    {
        var target = TargetPages(out _);
        var insert = InsertPages(out _);
        if (target == null || insert == null)
            return null;
        EditableDocument replacement;
        if (UseOpenDocument && SelectedOpenDocument!.Tab is { } tab)
        {
            if (await CopyFromTabAsync(tab) is not { } copy)
                return Failed("TabCopyFailed");
            // The tab's document may have changed since it was asked: all pages means all of the copy
            if (InsertAllPages)
                insert = [.. Enumerable.Range(0, copy.PageCount)];
            else if (insert.Any(p => p >= copy.PageCount))
            {
                await copy.DisposeAsync();
                return Failed("TabDocumentChanged");
            }
            replacement = copy;
        }
        else if (UseOpenDocument)
        {
            try
            {
                replacement = await SelectedOpenDocument!.Info!.Document.CopyAsync();
            }
            catch (PdfOpenException)
            {
                return Failed("ErrOpenFailed");
            }
        }
        else
        {
            replacement = _file!;
            _file = null;   // handed over to the caller
        }
        return new ReplacePagesResult(target[0], target.Count, replacement, insert);
    }

    private ReplacePagesResult? Failed(string errorKey)
    {
        _copyError = Loc.T(errorKey);
        OnPropertyChanged(nameof(SourceErrorText));
        return null;
    }

    /// <summary>Has the other tab write its document as it is now and opens that copy; null if it did not hand it over.</summary>
    private async Task<EditableDocument?> CopyFromTabAsync(TabDocumentInfo tab)
    {
        if (await _context.OtherTabs!.CopyDocumentAsync(tab) is not { } path)
            return null;
        try
        {
            return EditableDocument.FromSource(await PdfBackend.Current.OpenAsync(path));
        }
        catch (PdfOpenException)
        {
            return null;
        }
    }

    /// <summary>Closes a chosen file that was not handed over.</summary>
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_file != null)
            await _file.DisposeAsync();
        _file = null;
    }
}
