using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.App.ViewModels;

/// <summary>
/// One row of the concatenate list: a file (opened to count its pages), the current document or the document of another
/// window or browser tab (copied with its edits when it was added).
/// </summary>
public partial class ConcatItem(string? path, IPdfSource? source, int pageCount, string? error)
    : ObservableObject
{
    /// <summary>The entry that stands for the open document with its current edits.</summary>
    public static ConcatItem Current(int pageCount) => new(null, null, pageCount, null) { IsCurrent = true };

    /// <summary>The entry for another window's document: <paramref name="copy"/> holds its pages as they were when added.</summary>
    public static ConcatItem FromWindow(OpenDocumentInfo info, EditableDocument copy) =>
        new(info.Document.FilePath, null, copy.PageCount, null) { WindowName = info.Name, Origin = info.Document, Copy = copy, WasEdited = info.Document.IsDirty };

    /// <summary>Another window's document whose files could not be opened again.</summary>
    public static ConcatItem FailedWindow(OpenDocumentInfo info) =>
        new(info.Document.FilePath, null, 0, Loc.T("ConcatWindowFailed")) { WindowName = info.Name, Origin = info.Document };

    /// <summary>The entry for another browser tab's document: <paramref name="source"/> is the copy that tab wrote.</summary>
    public static ConcatItem FromTab(TabDocumentInfo document, string path, IPdfSource source) =>
        new(path, source, source.PageCount, null) { WindowName = document.Name, TabId = document.TabId, WasEdited = document.IsEdited };

    /// <summary>Another browser tab's document that could not be copied (the tab was closed or could not write it).</summary>
    public static ConcatItem FailedTab(TabDocumentInfo document) =>
        new(null, null, 0, Loc.T("ConcatTabFailed")) { WindowName = document.Name, TabId = document.TabId };

    public bool IsCurrent { get; private init; }

    /// <summary>The browser tab this entry was copied from; null for files, windows and the current document.</summary>
    public string? TabId { get; private init; }

    /// <summary>The document of the other window this entry was taken from; null for files and the current document.</summary>
    public EditableDocument? Origin { get; private init; }

    public bool IsFromWindow => Origin != null;

    private string? WindowName { get; init; }

    /// <summary>The copy of the other window's document (owned by this entry until the result is built).</summary>
    private EditableDocument? Copy { get; init; }

    private bool WasEdited { get; init; }

    /// <summary>File path; null for the current document.</summary>
    public string? Path { get; } = path;

    /// <summary>The opened file; null for the current document and for unreadable files.</summary>
    public IPdfSource? Source { get; } = source;

    public int PageCount { get; } = pageCount;

    /// <summary>Localized reason why the file cannot be used; null if fine.</summary>
    public string? Error { get; } = error;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool HasError => Error != null;

    public string Name => IsCurrent ? Loc.T("ConcatCurrentDocument") : WindowName ?? System.IO.Path.GetFileName(Path) ?? "";

    public string Folder => IsCurrent ? ""
        : TabId != null ? Loc.T(WasEdited ? "ConcatFromTabEdited" : "ConcatFromTab")
        : IsFromWindow ? Loc.T(WasEdited ? "ConcatFromWindowEdited" : "ConcatFromWindow")
        : Services.PlatformFeatures.Current.IsWeb ? "" // uploaded files have no folder of their own
        : System.IO.Path.GetDirectoryName(Path) ?? "";

    public string PagesText => HasError ? Error : Loc.Pages(PageCount);

    /// <summary>The document for the result (null for the current document); hands over the opened source or copy.</summary>
    internal EditableDocument? ToDocument() => Copy ?? (Source != null ? EditableDocument.FromSource(Source) : null);

    internal async ValueTask DisposeAsync()
    {
        if (Source != null)
            await Source.DisposeAsync();
        if (Copy != null)
            await Copy.DisposeAsync();
    }
}

public partial class ConcatenateDialogViewModel : ObservableObject
{
    private readonly int _currentPageCount;
    private bool _includeCurrent;

    private readonly Func<IReadOnlyList<OpenDocumentInfo>>? _otherDocuments;
    private readonly IBrowserTabs? _tabs;

    public ConcatenateDialogViewModel(ConcatenateContext context, int currentPageCount)
    {
        HasCurrentDocument = context.HasCurrentDocument;
        _otherDocuments = context.OtherDocuments;
        _tabs = context.OtherTabs;
        _hasOtherDocuments = OtherDocuments().Count > 0;
        _currentPageCount = currentPageCount;
        IncludeCurrent = context.HasCurrentDocument;
        Items.CollectionChanged += (_, _) => Changed();
    }

    public ObservableCollection<ConcatItem> Items { get; } = [];

    public bool HasCurrentDocument { get; }

    /// <summary>
    /// Whether other windows had a document when the dialog opened, or other browser tabs answered with one (shows the
    /// "Open documents" button that adds them all).
    /// </summary>
    [ObservableProperty]
    private bool _hasOtherDocuments;

    /// <summary>Web version: asks the other browser tabs whether they have a document (when the dialog is shown).</summary>
    public async Task FindOtherTabsAsync()
    {
        if (_tabs != null && (await _tabs.OtherDocumentsAsync()).Count > 0)
            HasOtherDocuments = true;
    }

    /// <summary>The documents of the other windows right now (they can be opened or closed while the dialog is shown).</summary>
    public IReadOnlyList<OpenDocumentInfo> OtherDocuments() => _otherDocuments?.Invoke() ?? [];

    public bool IsListed(OpenDocumentInfo info) => Items.Any(i => i.Origin == info.Document);

    [ObservableProperty]
    private ConcatItem? _selected;

    /// <summary>Adds/removes the "(current document)" entry (at the top).</summary>
    public bool IncludeCurrent
    {
        get => _includeCurrent;
        set
        {
            if (_includeCurrent == value)
                return;
            _includeCurrent = value;
            if (value && !Items.Any(i => i.IsCurrent))
                Items.Insert(0, ConcatItem.Current(_currentPageCount));
            else if (!value)
                foreach (var item in Items.Where(i => i.IsCurrent).ToList())
                    Items.Remove(item);
            OnPropertyChanged();
        }
    }

    /// <summary>Opens the files (to count their pages) and appends them; unreadable ones are added marked as errors.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p)))
        {
            try
            {
                var source = await PdfBackend.Current.OpenAsync(path);
                Items.Add(new ConcatItem(path, source, source.PageCount, null));
            }
            catch (PdfOpenException ex)
            {
                Items.Add(new ConcatItem(path, null, 0, Loc.T(ex.IsPasswordProtected ? "ErrPasswordProtected" : "ErrNotAPdf")));
            }
        }
    }

    /// <summary>
    /// Appends another window's document as it is now (with its unsaved changes): its files are opened again, so the entry
    /// does not depend on that window staying open. Already listed documents are not added twice.
    /// </summary>
    private async Task AddOpenDocumentAsync(OpenDocumentInfo info)
    {
        if (IsListed(info))
            return;
        try
        {
            Items.Add(ConcatItem.FromWindow(info, await info.Document.CopyAsync()));
        }
        catch (PdfOpenException)
        {
            Items.Add(ConcatItem.FailedWindow(info));
        }
    }

    /// <summary>
    /// The "Open documents" button: appends the documents of all other windows (web version: browser tabs) at once, except
    /// those already listed (unwanted ones are removed from the list; a removed one comes back with the next click).
    /// </summary>
    [RelayCommand]
    private async Task AddAllOpenDocumentsAsync()
    {
        foreach (var info in OtherDocuments())
            await AddOpenDocumentAsync(info);
        if (_tabs != null)
            foreach (var document in await _tabs.OtherDocumentsAsync())
                await AddTabDocumentAsync(_tabs, document);
    }

    /// <summary>
    /// Appends another browser tab's document as it is now: that tab has its document written as a new file (with its
    /// unsaved changes), which this entry then owns.
    /// </summary>
    private async Task AddTabDocumentAsync(IBrowserTabs tabs, TabDocumentInfo document)
    {
        if (Items.Any(i => i.TabId == document.TabId))
            return;
        if (await tabs.CopyDocumentAsync(document) is not { } path)
        {
            Items.Add(ConcatItem.FailedTab(document));
            return;
        }
        try
        {
            var source = await PdfBackend.Current.OpenAsync(path);
            Items.Add(ConcatItem.FromTab(document, path, source));
        }
        catch (PdfOpenException)
        {
            Items.Add(ConcatItem.FailedTab(document));
        }
    }

    public void Remove(ConcatItem? item)
    {
        if (item == null)
            return;
        Items.Remove(item);
        if (item.IsCurrent)
        {
            _includeCurrent = false;
            OnPropertyChanged(nameof(IncludeCurrent));
        }
        _ = item.DisposeAsync();
    }

    /// <summary>
    /// Moves an entry so that it ends up before position <paramref name="insertIndex"/> (0..Count, counted in the list as it is
    /// before the move). Used by drag and drop.
    /// </summary>
    public void MoveTo(ConcatItem item, int insertIndex)
    {
        var index = Items.IndexOf(item);
        if (index < 0)
            return;
        var target = Math.Clamp(insertIndex > index ? insertIndex - 1 : insertIndex, 0, Items.Count - 1);
        if (target == index)
            return;
        Items.Move(index, target);
        Selected = item;
    }

    public void MoveUp(ConcatItem? item) => Move(item, -1);

    public void MoveDown(ConcatItem? item) => Move(item, 1);

    private void Move(ConcatItem? item, int delta)
    {
        if (item == null)
            return;
        var index = Items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Items.Count)
            return;
        Items.Move(index, target);
        Selected = item;
    }

    /// <summary>Sorts the files and window documents by name; the current document keeps its place at the top.</summary>
    public void SortByName()
    {
        var current = Items.Where(i => i.IsCurrent).ToList();
        var files = Items.Where(i => !i.IsCurrent).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Items.Clear();
        foreach (var item in current.Concat(files))
            Items.Add(item);
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(TotalPages));
    }

    public int TotalPages => Items.Where(i => !i.HasError).Sum(i => i.PageCount);

    /// <summary>OK needs at least two entries and none of them broken.</summary>
    public bool IsValid => Items.Count >= 2 && Items.All(i => !i.HasError);

    public string HintText => Items.Any(i => i.HasError) ? Loc.T("ConcatRemoveBroken") : Items.Count < 2 ? Loc.T("ConcatNeedTwo") : Loc.F("ConcatTotal", Items.Count, TotalPages);

    /// <summary>The result for OK (null if invalid). The caller takes over the opened sources and copies.</summary>
    public ConcatenateResult? BuildResult() => IsValid ? new ConcatenateResult([.. Items.Select(i => i.ToDocument())]) : null;

    /// <summary>Releases all opened files (Cancel).</summary>
    public async Task DisposeAllAsync()
    {
        foreach (var item in Items.ToList())
            await item.DisposeAsync();
        Items.Clear();
    }
}
