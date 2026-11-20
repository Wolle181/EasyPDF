using System;
using System.Collections.Generic;
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

/// <summary>An entry of the "open document" list: another window's document.</summary>
public sealed record ReplaceDocumentItem(OpenDocumentInfo Info)
{
    public string Label => Loc.F("ReplaceDocumentEntry", Info.Name, Loc.Pages(Info.Document.PageCount));
}

/// <summary>
/// The "Replace pages" dialog: consecutive pages of the open document are removed and pages of another open document or a
/// file take their place. A chosen file is opened right away (page count, errors) and closed again unless it is used.
/// </summary>
public partial class ReplacePagesDialogViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ReplacePagesContext _context;
    private EditableDocument? _file;

    public ReplacePagesDialogViewModel(ReplacePagesContext context)
    {
        _context = context;
        _target = context.SelectedPages.Count > 1 ? ReplaceTarget.Selected : ReplaceTarget.Current;
        OpenDocuments = [.. (context.OtherDocuments?.Invoke() ?? []).Select(d => new ReplaceDocumentItem(d))];
        _selectedOpenDocument = OpenDocuments.FirstOrDefault();
        _useOpenDocument = HasOpenDocuments;
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

    public IReadOnlyList<ReplaceDocumentItem> OpenDocuments { get; }

    /// <summary>Whether other windows had a document when the dialog opened; otherwise only a file can be chosen.</summary>
    public bool HasOpenDocuments => OpenDocuments.Count > 0;

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

    /// <summary>The document the new pages come from (not yet copied for an open document); null if none is usable.</summary>
    private EditableDocument? ReplacementDocument => UseOpenDocument ? SelectedOpenDocument?.Info.Document : _file;

    private string ReplacementName => UseOpenDocument ? SelectedOpenDocument?.Info.Name ?? "" : Path.GetFileName(FilePath ?? "");

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
        var document = ReplacementDocument;
        if (document == null)
            return null;
        if (InsertAllPages)
            return [.. Enumerable.Range(0, document.PageCount)];
        var range = PageRangeParser.Parse(InsertRangeText, document.PageCount);
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
            if (ReplacementDocument == null)
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
        if (UseOpenDocument)
        {
            try
            {
                replacement = await SelectedOpenDocument!.Info.Document.CopyAsync();
            }
            catch (PdfOpenException)
            {
                _copyError = Loc.T("ErrOpenFailed");
                OnPropertyChanged(nameof(SourceErrorText));
                return null;
            }
        }
        else
        {
            replacement = _file!;
            _file = null;   // handed over to the caller
        }
        return new ReplacePagesResult(target[0], target.Count, replacement, insert);
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
