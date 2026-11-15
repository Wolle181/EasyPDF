using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.App.Controls;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Layout;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Templates;

namespace EasyPDF.App.ViewModels;

/// <param name="ocrEngineFactory">Creates the text recognizer on first use (tests pass a fake); default: <see cref="RapidOcrEngine"/>.</param>
/// <param name="windows">The other main windows; null: this view model is alone and replaces its document when opening a file.</param>
/// <param name="mail">Opens messages in the email program (tests pass a fake); default: <see cref="PlatformMailService"/>.</param>
public partial class MainViewModel(AppSettings settings, IDialogService dialogs, IPrintService? printing = null, Func<IOcrEngine>? ocrEngineFactory = null,
    IDocumentWindows? windows = null, IMailService? mail = null)
    : ObservableObject
{
    private static readonly double[] _zoomSteps = [10, 25, 50, 75, 100, 125, 150, 200, 300, 400, 600, 800];
    private static readonly double[] _thumbnailZoomSteps = [50, 75, 100, 125, 150, 200, 250, 300];

    private int _lastRotateDegrees = 90;
    private string? _suggestedFileName;
    private System.Threading.CancellationTokenSource? _operation;
    private HashSet<int> _selectedIds = [];

    [ObservableProperty]
    private EditableDocument? _document;

    [ObservableProperty]
    private ZoomMode _zoomMode = settings.ZoomMode;

    [ObservableProperty]
    private double _customZoomPercent = settings.CustomZoomPercent;

    [ObservableProperty]
    private int _currentPage;

    /// <summary><see cref="PageEntry.Id"/> of the page clicked last in the reading view (red frame), -1 = none.</summary>
    [ObservableProperty]
    private int _markedPageId = -1;

    [ObservableProperty]
    private double _effectiveZoomPercent = 100;

    /// <summary>Thumbnail size in Organize mode, in percent of the standard size.</summary>
    [ObservableProperty]
    private double _thumbnailZoomPercent = Math.Clamp(settings.ThumbnailZoomPercent, ThumbnailGrid.MinZoomPercent, ThumbnailGrid.MaxZoomPercent);

    [ObservableProperty]
    private string _zoomInput = "100";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double _progress;

    /// <summary>What the running operation does (status bar, next to the progress bar).</summary>
    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    private bool _isRibbonCollapsed = settings.RibbonCollapsed;

    [ObservableProperty]
    private bool _isOrganizeMode;

    /// <summary>The page thumbnails on the left of the reading view are switched on (setting).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSidebarShown))]
    private bool _isPageSidebarVisible = settings.PageSidebarVisible;

    public const double MinPageSidebarWidth = 100;
    public const double MaxPageSidebarWidth = 600;

    /// <summary>Width of the page sidebar in px (dragged at its right edge; the thumbnails fill it); remembered in the settings.</summary>
    [ObservableProperty]
    private double _pageSidebarWidth = ClampSidebarWidth(settings.PageSidebarWidth);

    private static double ClampSidebarWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinPageSidebarWidth, MaxPageSidebarWidth) : AppSettings.DefaultPageSidebarWidth;

    /// <summary>Changes the width of the page sidebar, within its limits and at most <paramref name="maxWidth"/> (part of the window).</summary>
    public void ResizePageSidebar(double width, double maxWidth = MaxPageSidebarWidth) =>
        PageSidebarWidth = ClampSidebarWidth(Math.Min(width, Math.Max(MinPageSidebarWidth, maxWidth)));

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>Cancels the running save, extract or split (Cancel button next to the progress bar, Esc).</summary>
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void CancelOperation() => _operation?.Cancel();

    #region Derived state

    [MemberNotNullWhen(true, nameof(Document))]
    public bool HasDocument => Document != null;

    /// <summary>The document for commands, which only run while one is open (see their CanExecute).</summary>
    private EditableDocument OpenDocument => Document ?? throw new InvalidOperationException("No document is open.");

    public bool IsDirty => Document is { IsDirty: true };

    public bool ReadingVisible => HasDocument && !IsOrganizeMode;

    /// <summary>The page sidebar belongs to the reading view (Organize mode shows thumbnails anyway).</summary>
    public bool PageSidebarShown => ReadingVisible && IsPageSidebarVisible;

    public bool OrganizeVisible => HasDocument && IsOrganizeMode;

    public bool IsReadingMode => !IsOrganizeMode;

    /// <summary>Ids of the selected pages (a new set instance on every change, so bindings update).</summary>
    public IReadOnlyCollection<int> SelectedIds => _selectedIds;

    public bool HasSelection => _selectedIds.Count > 0;

    public IReadOnlyList<string> RecentFiles => settings.RecentFiles;

    public string Title
    {
        get
        {
            if (Document == null)
                return Loc.T("AppName");
            return Loc.F(IsDirty ? "TitleWithFileDirty" : "TitleWithFile", DocumentName);
        }
    }

    /// <summary>File name of the document, "Untitled" while it has no file (as in the window title).</summary>
    public string DocumentName => Document?.FilePath is { } path ? Path.GetFileName(path) : Loc.T("Untitled");

    public string StatusPageText => Document == null
        ? Loc.T("StatusNoDocument")
        : Loc.F("StatusPage", CurrentPage + 1, Document.PageCount);

    /// <summary>The zoom the View commands act on: page zoom in Reading mode, thumbnail size in Organize mode.</summary>
    public double ShownZoomPercent => IsOrganizeMode ? ThumbnailZoomPercent : EffectiveZoomPercent;

    /// <summary>Whole-number zoom as shown in the ribbon box and the status bar (wheel zoom yields fractions).</summary>
    private string ShownZoomRounded => ShownZoomPercent.ToString("0");

    public string StatusZoomText => Document == null ? "" : Loc.F("StatusZoom", ShownZoomRounded);

    public string ProgressText => Loc.F("StatusProgress", Math.Round(Progress * 100));

    /// <summary>"Unsaved changes"; mentions a recognized text layer, which is not visible on the page until saved.</summary>
    public string StatusDirtyText => Document is not { IsDirty: true } document ? ""
        : document.OcrPageCount > 0 ? Loc.F("StatusUnsavedOcr", Loc.Pages(document.OcrPageCount))
        : Loc.T("StatusUnsaved");

    /// <summary>Raised when the list of recent files changed (menus rebuild themselves).</summary>
    public event Action? RecentFilesChanged;

    /// <summary>Raised on the UI thread whenever more page sizes are known.</summary>
    public event Action? PageSizesLoaded;

    /// <summary>Asks the view to scroll to a page (zero-based).</summary>
    public event Action<int>? ScrollToPageRequested;

    /// <summary>Asks the view to open the "Go to page" box in the status bar.</summary>
    public event Action? GoToPageBoxRequested;

    /// <summary>Re-reads all texts (after a language change).</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusPageText));
        OnPropertyChanged(nameof(StatusZoomText));
        OnPropertyChanged(nameof(StatusDirtyText));
        OnPropertyChanged(nameof(SearchStatusText));
    }

    partial void OnDocumentChanging(EditableDocument? value)
    {
        if (Document != null)
            Document.Changed -= OnDocumentEdited;
    }

    partial void OnDocumentChanged(EditableDocument? value)
    {
        if (value != null)
            value.Changed += OnDocumentEdited;
        else
        {
            IsSelectTextToolActive = false;
            ActiveRedaction = null;
            IsCopyAreaToolActive = false;
            PendingPaste = null;
            CloseSearch();
        }
        RefreshState();
    }

    private void OnDocumentEdited(DocumentChangeKind kind) => RefreshState();

    partial void OnCurrentPageChanged(int value) => OnPropertyChanged(nameof(StatusPageText));

    partial void OnEffectiveZoomPercentChanged(double value) => ShownZoomChanged();

    partial void OnThumbnailZoomPercentChanged(double value)
    {
        settings.ThumbnailZoomPercent = value;
        ShownZoomChanged();
    }

    private void ShownZoomChanged()
    {
        OnPropertyChanged(nameof(ShownZoomPercent));
        OnPropertyChanged(nameof(StatusZoomText));
        ZoomInput = ShownZoomRounded;
    }

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    partial void OnZoomModeChanged(ZoomMode value) => settings.ZoomMode = value;

    partial void OnCustomZoomPercentChanged(double value) => settings.CustomZoomPercent = value;

    partial void OnIsRibbonCollapsedChanged(bool value) => settings.RibbonCollapsed = value;

    partial void OnIsPageSidebarVisibleChanged(bool value) => settings.PageSidebarVisible = value;

    partial void OnPageSidebarWidthChanged(double value) => settings.PageSidebarWidth = value;

    partial void OnIsOrganizeModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ReadingVisible));
        OnPropertyChanged(nameof(PageSidebarShown));
        OnPropertyChanged(nameof(OrganizeVisible));
        OnPropertyChanged(nameof(IsReadingMode));
        if (value)
        {
            IsSelectTextToolActive = false; // text is selected, boxes are drawn and areas copied in the reading view
            ActiveRedaction = null;
            IsCopyAreaToolActive = false;
            PendingPaste = null;
        }
        ShownZoomChanged();
        RefreshCommands();
        ScrollToPageRequested?.Invoke(CurrentPage); // the view that just became visible shows the current page
    }

    /// <summary>Called by the Organize view when the user changes the selection.</summary>
    public void SetSelection(IEnumerable<int> ids)
    {
        _selectedIds = [.. ids];
        OnPropertyChanged(nameof(SelectedIds));
        OnPropertyChanged(nameof(HasSelection));
        RefreshCommands();
    }

    private void PruneSelection()
    {
        if (_selectedIds.Count == 0)
            return;
        var existing = Document == null ? new HashSet<int>() : Document.Pages.Select(p => p.Id).ToHashSet();
        if (_selectedIds.Any(id => !existing.Contains(id)))
            SetSelection([.. _selectedIds.Where(existing.Contains)]);
    }

    /// <summary>Zero-based indices of the selected pages, ascending.</summary>
    private int[] SelectedIndices() =>
        Document == null ? [] :
        [
            .. Enumerable.Range(0, Document.PageCount).Where(i => _selectedIds.Contains(Document.Pages[i].Id))
        ];

    /// <summary>
    /// Pages a page command acts on: in Organize mode the selection (if the clicked page belongs to it or no page was given),
    /// otherwise the clicked or current page.
    /// </summary>
    private int[] TargetPages(int? page)
    {
        var selected = IsOrganizeMode ? SelectedIndices() : [];
        if (selected.Length > 0 && (page is not >= 0 || selected.Contains(page.Value)))
            return selected;
        return [ResolvePage(page)];
    }

    /// <summary>
    /// What a page dialog starts with: the selected pages (Organize mode) and the page it offers as "current page", which is
    /// the selected page if exactly one is selected, otherwise the clicked or current page.
    /// </summary>
    private (int Current, int[] Selected) DialogPages(int? page)
    {
        var selected = IsOrganizeMode ? SelectedIndices() : [];
        return (selected.Length == 1 ? selected[0] : ResolvePage(page), selected);
    }

    /// <summary>How long a status message stays visible.</summary>
    public static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(6);

    /// <summary>Waits before a status message disappears; tests replace it to control time.</summary>
    public Func<TimeSpan, System.Threading.CancellationToken, Task> Delay { get; set; } = Task.Delay;

    private System.Threading.CancellationTokenSource? _messageTimer;

    private void ShowMessage(string text)
    {
        StatusMessage = text;
        // A new message restarts the timer, even when its text equals the previous one.
        _messageTimer?.Cancel();
        _messageTimer?.Dispose();
        _messageTimer = new System.Threading.CancellationTokenSource();
        _ = ClearMessageLaterAsync(text, _messageTimer.Token);
    }

    private async Task ClearMessageLaterAsync(string text, System.Threading.CancellationToken token)
    {
        try
        {
            await Delay(MessageDuration, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        // await resumes on the UI thread, so the property may be changed here directly.
        if (StatusMessage == text)
            StatusMessage = "";
    }

    private void RefreshState()
    {
        PruneSelection();
        OnPropertyChanged(nameof(ReadingVisible));
        OnPropertyChanged(nameof(OrganizeVisible));
        OnPropertyChanged(nameof(PageSidebarShown));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusPageText));
        OnPropertyChanged(nameof(StatusZoomText));
        OnPropertyChanged(nameof(StatusDirtyText));
        RefreshCommands();
        RefreshSearch();
    }

    private void RefreshCommands()
    {
        foreach (var c in new IRelayCommand[]
                 {
                     SaveCommand, SaveAsCommand, CancelOperationCommand, CloseDocumentCommand, UndoCommand, RedoCommand, InsertBeforeCommand,
                     InsertAfterCommand, InsertBlankBeforeCommand, InsertBlankAfterCommand, ReplacePagesCommand, RotatePagesCommand, RotateLeftCommand, RotateRightCommand, DeletePagesCommand,
                     OpenCommand, FitHeightCommand, FitWidthCommand, FitBestCommand, ZoomInCommand, ZoomOutCommand,
                     ActualSizeCommand, ZoomToCommand, ApplyZoomInputCommand, ShowReadingCommand, ShowOrganizeCommand, SwitchModeCommand,
                     SelectAllCommand, ExtractPagesCommand, SplitDocumentCommand, ConcatenateCommand, GoToFirstPageCommand, GoToLastPageCommand,
                     ShowGoToPageCommand, GoToTypedPageCommand, SelectTextToolCommand, PrintCommand, SendByMailCommand, RecognizeTextCommand,
                     CopyTextCommand, ReduceSizeCommand, ToggleSelectTextToolCommand, RedactCommand, CoverCommand, ToggleCopyAreaToolCommand,
                     PasteAreaCommand, ShowSearchCommand, FindNextCommand, FindPreviousCommand,
                 })
            c.NotifyCanExecuteChanged();
    }

    /// <summary>Starts a long operation (save, extract, split): the status bar shows <paramref name="text"/>, progress and Cancel.</summary>
    [MemberNotNull(nameof(_operation))]
    private void BeginOperation(string text)
    {
        BusyText = text;
        Progress = 0;
        _operation = new System.Threading.CancellationTokenSource();
        IsBusy = true;
    }

    private bool CanUseDocument() => HasDocument && !IsBusy;

    private bool CanSave() => HasDocument && !IsBusy && (IsDirty || !Document.HasFile);

    private bool CanDelete() => HasDocument && !IsBusy && Document.PageCount > 1;

    private bool CanUndo() => HasDocument && !IsBusy && Document.CanUndo;

    private bool CanRedo() => HasDocument && !IsBusy && Document.CanRedo;

    private bool NotBusy() => !IsBusy;

    /// <summary>
    /// Command parameter → page index; null/negative means "the page marked by a click in the reading view",
    /// or the current page if there is none.
    /// </summary>
    private int ResolvePage(int? page)
    {
        var document = OpenDocument;
        if (page is >= 0 && page < document.PageCount)
            return page.Value;
        if (!IsOrganizeMode && MarkedPageId >= 0)
        {
            for (var i = 0; i < document.PageCount; i++)
                if (document.Pages[i].Id == MarkedPageId)
                    return i;
        }
        return Math.Clamp(CurrentPage, 0, document.PageCount - 1);
    }

    /// <summary>File name without extension for derived files; "Untitled" for documents that were never saved.</summary>
    private static string BaseNameOf(EditableDocument document) =>
        document.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : Loc.T("Untitled");

    /// <summary>Folder of the document's file; null for documents that were never saved.</summary>
    private static string? FolderOf(EditableDocument document) =>
        document.FilePath is { } path ? Path.GetDirectoryName(path) : null;

    #endregion

    #region Opening and closing

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenAsync()
    {
        var path = await dialogs.PickPdfFileAsync();
        if (path != null)
            await OpenPathAsync(path);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            settings.RemoveRecentFile(path);
            RecentFilesChanged?.Invoke();
            await dialogs.ShowErrorAsync(Loc.T("ErrRecentMissing"), path);
            return;
        }
        await OpenPathAsync(path);
    }

    [RelayCommand]
    private void ClearRecent()
    {
        settings.ClearRecentFiles();
        RecentFilesChanged?.Invoke();
    }

    private bool CanOpenNewWindow() => windows != null;

    [RelayCommand(CanExecute = nameof(CanOpenNewWindow))]
    private void NewWindow() => windows?.OpenNewWindow();

    /// <summary>
    /// Opens a file: in this window if it has no document, otherwise in a new window. A file that already is the
    /// document of a window is not opened twice, that window comes to the front. Alone (without other windows) the file
    /// replaces the document after asking about unsaved changes.
    /// </summary>
    public async Task OpenPathAsync(string path)
    {
        if (windows != null && windows.ActivateWindowShowing(path))
            return;
        var inNewWindow = windows != null && HasDocument;
        if (!inNewWindow && !await ConfirmDiscardAsync())
            return;

        PdfSource source;
        try
        {
            source = await PdfSource.OpenAsync(path);
        }
        catch (PdfOpenException ex)
        {
            await dialogs.ShowErrorAsync(OpenErrorText(ex), path);
            return;
        }

        if (windows != null && inNewWindow)
            await windows.OpenInNewWindowAsync(this, vm => vm.ShowSourceAsync(source));
        else
            await ShowSourceAsync(source);
    }

    private async Task ShowSourceAsync(PdfSource source)
    {
        var old = Document;
        _suggestedFileName = null;
        Document = EditableDocument.FromSource(source);
        CurrentPage = 0;
        SetSelection([]);
        settings.AddRecentFile(source.Path);
        RecentFilesChanged?.Invoke();
        if (old != null)
            await old.DisposeAsync();

        // Page sizes arrive in the background; the layout starts with the first page's size as estimate
        await LoadSizesAsync(source);
    }

    private async Task LoadSizesAsync(PdfSource source)
    {
        await source.LoadPageSizesAsync(new Progress<int>(_ => PageSizesLoaded?.Invoke()));
        PageSizesLoaded?.Invoke();
    }

    private static string OpenErrorText(PdfOpenException ex) =>
        Loc.T(ex.IsPasswordProtected ? "ErrPasswordProtected" : ex.ErrorCode == 3 ? "ErrNotAPdf" : "ErrOpenFailed");

    /// <summary>Closes the document; the window closes too if other windows are open.</summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private async Task CloseDocumentAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;
        var old = Document;
        Document = null;
        CurrentPage = 0;
        SetSelection([]);
        if (old != null)
            await old.DisposeAsync();
        if (windows is { Count: > 1 })
            dialogs.CloseWindow();
    }

    /// <summary>
    /// The window was closed: gives up the document without asking, so that none of its files stays open (the process
    /// lives on while other windows are open, and an open file cannot be deleted or replaced). Without a document it
    /// returns the release that is still running, if any.
    /// </summary>
    public Task ReleaseDocumentAsync()
    {
        _operation?.Cancel();
        var old = Document;
        if (old == null)
            return _release;
        Document = null;
        return _release = old.DisposeAsync().AsTask();
    }

    private Task _release = Task.CompletedTask;

    /// <summary>True if it is fine to replace/close the document (nothing unsaved, saved, or discarded).</summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (Document is not { IsDirty: true })
            return true;
        var name = Document.FilePath is { } path ? Path.GetFileName(path) : Loc.T("Untitled");
        switch (await dialogs.AskUnsavedChangesAsync(name))
        {
            case UnsavedChoice.Save:
                return await SaveCoreAsync(saveAs: false);
            case UnsavedChoice.Discard:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Ends the program: closes all windows.</summary>
    [RelayCommand]
    private async Task ExitAsync()
    {
        if (windows != null)
            await windows.CloseAllAsync();
        else if (await ConfirmDiscardAsync())
            dialogs.CloseWindow();
    }

    #endregion

    #region Saving

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SaveCoreAsync(saveAs: false);

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private Task SaveAsAsync() => SaveCoreAsync(saveAs: true);

    private async Task<bool> SaveCoreAsync(bool saveAs)
    {
        var document = Document;
        if (document == null)
            return true;

        var path = document.FilePath;
        if (saveAs || path == null)
        {
            var suggested = path != null ? Path.GetFileName(path) : _suggestedFileName ?? Loc.T("DefaultFileName");
            path = await dialogs.PickSavePathAsync(suggested, path != null ? Path.GetDirectoryName(path) : null);
            if (path == null)
                return false;
        }
        path = await ConfirmRedactedTargetAsync(document, path);
        if (path == null)
            return false;
        return await WriteAsync(document, path, null, Loc.T("BusySaving")) != null;
    }

    /// <summary>
    /// Before blacked-out pages are saved over the document's own file (their content is then gone for good): asks
    /// whether to overwrite it or save a copy, unless the warning is switched off in the settings. Returns the path to
    /// write to; null = cancelled.
    /// </summary>
    private async Task<string?> ConfirmRedactedTargetAsync(EditableDocument document, string path)
    {
        if (!settings.WarnOverwriteRedacted || document.RedactedPageCount == 0
            || document.FilePath is not { } original || !EditableDocument.PathsEqual(path, original))
            return path;
        var answer = await dialogs.AskOverwriteRedactedAsync(Path.GetFileName(original), document.RedactedPageCount);
        if (answer.Choice == RedactedSaveChoice.Cancel)
            return null;
        if (answer.DontAskAgain)
            settings.WarnOverwriteRedacted = false; // can be switched on again in the settings
        return answer.Choice == RedactedSaveChoice.Overwrite
            ? path
            : await dialogs.PickSavePathAsync(Loc.F("RedactedFileName", BaseNameOf(document)), FolderOf(document));
    }

    private ImageReduction _lastReduction = ImageReduction.Medium;

    /// <summary>
    /// Saves the document under a new name with its pictures scaled down (the original file stays as it is); the window
    /// then shows the smaller file. Tells how much smaller it got.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private async Task ReduceSizeAsync()
    {
        var document = OpenDocument;
        var reduction = await dialogs.ShowReduceSizeDialogAsync(_lastReduction);
        if (reduction == null)
            return;
        _lastReduction = reduction;
        // The suggested name tells the level: scan_medium.pdf
        var nameKey = reduction == ImageReduction.High ? "ReduceFileNameHigh"
            : reduction == ImageReduction.Small ? "ReduceFileNameSmall"
            : "ReduceFileNameMedium";
        var path = await dialogs.PickSavePathAsync(Loc.F(nameKey, BaseNameOf(document)), FolderOf(document));
        if (path != null)
            path = await ConfirmRedactedTargetAsync(document, path);
        if (path == null)
            return;

        var before = document.FilePath is { } original && File.Exists(original) ? new FileInfo(original).Length : (long?)null;
        var reduced = await WriteAsync(document, path, reduction, Loc.T("BusyReducing"));
        if (reduced == null)
            return;
        var after = new FileInfo(path).Length;
        var message = reduced == 0 ? Loc.T("MsgReducedNothing")
            : before is { } size && size > after ? Loc.F("MsgReduced", reduced, FormatSize(size), FormatSize(after), (int)Math.Round(100.0 * (size - after) / size))
            : Loc.F("MsgReducedNoComparison", reduced, FormatSize(after));
        await dialogs.ShowInfoAsync(message, path);
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? string.Format(System.Globalization.CultureInfo.CurrentUICulture, "{0:0.0} MB", bytes / (1024.0 * 1024))
        : string.Format(System.Globalization.CultureInfo.CurrentUICulture, "{0:0} KB", Math.Ceiling(bytes / 1024.0));

    /// <summary>
    /// Writes the document to <paramref name="path"/> (optionally with its pictures scaled down) and adds it to the
    /// recent files; returns how many pictures were scaled down, or null if it failed or was cancelled.
    /// </summary>
    private async Task<int?> WriteAsync(EditableDocument document, string path, ImageReduction? reduction, string busyText)
    {
        // Another window reads from that file: it could not be replaced
        if (windows != null && windows.IsFileUsedByOtherWindow(path, this))
        {
            await dialogs.ShowErrorAsync(Loc.T("ErrFileOpenInOtherWindow"), path);
            return null;
        }

        BeginOperation(busyText);
        try
        {
            var reduced = await document.SaveAsync(path, new Progress<double>(p => Progress = p), _operation.Token, reduction);
            settings.AddRecentFile(path);
            RecentFilesChanged?.Invoke();
            return reduced;
        }
        catch (OperationCanceledException)
        {
            ShowMessage(Loc.T("MsgCancelled"));
            return null;
        }
        catch (Exception ex)
        {
            await dialogs.ShowErrorAsync(Loc.T("ErrSaveFailed"), ex.Message);
            return null;
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            IsBusy = false;
            RefreshState();
        }
    }

    #endregion

    #region Editing

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => OpenDocument.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => OpenDocument.Redo();

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private Task InsertBeforeAsync(int? page) =>
        InsertAsync(page is >= 0 || !IsOrganizeMode || !HasSelection ? ResolvePage(page) : SelectedIndices().First());

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private Task InsertAfterAsync(int? page) =>
        InsertAsync((page is >= 0 || !IsOrganizeMode || !HasSelection ? ResolvePage(page) : SelectedIndices().Last()) + 1);

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private Task InsertBlankBeforeAsync(int? page) =>
        InsertBlankAsync(page is >= 0 || !IsOrganizeMode || !HasSelection ? ResolvePage(page) : SelectedIndices().First());

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private Task InsertBlankAfterAsync(int? page) =>
        InsertBlankAsync((page is >= 0 || !IsOrganizeMode || !HasSelection ? ResolvePage(page) : SelectedIndices().Last()) + 1);

    /// <summary>Inserts an empty page at <paramref name="index"/>, as large as the page next to it.</summary>
    private async Task InsertBlankAsync(int index)
    {
        var document = OpenDocument;
        var neighbour = document.Pages[Math.Clamp(index > 0 ? index - 1 : 0, 0, document.PageCount - 1)];
        var first = await document.InsertBlankAsync(index, neighbour.DisplaySize);
        ScrollToPageRequested?.Invoke(first);
    }

    private async Task InsertAsync(int index)
    {
        var files = await dialogs.PickPdfFilesAsync(Loc.T("PickInsertTitle"));
        if (files.Count == 0)
            return;

        var sources = new List<PdfSource>();
        foreach (var file in files)
        {
            try
            {
                sources.Add(await PdfSource.OpenAsync(file));
            }
            catch (PdfOpenException ex)
            {
                await dialogs.ShowErrorAsync(OpenErrorText(ex), file);
            }
        }
        if (sources.Count == 0)
            return;

        int first;
        if (sources.Count == 1)
        {
            // A single file: let the user pick which of its pages to take
            var source = sources[0];
            var pages = await dialogs.ShowInsertRangeDialogAsync(
                new InsertRangeContext(System.IO.Path.GetFileName(source.Path), source.PageCount));
            if (pages == null)
            {
                await source.DisposeAsync();
                return;
            }
            first = OpenDocument.InsertFrom(source, pages, index);
        }
        else
            first = OpenDocument.InsertFrom(sources, index);
        ScrollToPageRequested?.Invoke(first);
        foreach (var source in sources)
            await LoadSizesAsync(source);
    }

    /// <summary>Replaces consecutive pages by pages of another open document or a file (one undo step).</summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private async Task ReplacePagesAsync(int? page)
    {
        var document = OpenDocument;
        var (current, selected) = DialogPages(page);
        var otherDocuments = windows == null ? null : (Func<IReadOnlyList<OpenDocumentInfo>>)(() => windows.OtherDocuments(this));
        var result = await dialogs.ShowReplacePagesDialogAsync(new ReplacePagesContext(document.PageCount, current, selected, otherDocuments));
        if (result == null)
            return;
        if (Document != document)   // the document was closed meanwhile
        {
            await result.Replacement.DisposeAsync();
            return;
        }
        var first = document.Replace(result.First, result.Count, result.Replacement, result.Pages);
        ScrollToPageRequested?.Invoke(first);
        foreach (var source in result.Replacement.Sources)
            await LoadSizesAsync(source);
    }

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private async Task RotatePagesAsync(int? page)
    {
        var document = OpenDocument;
        var (current, selected) = DialogPages(page);
        var context = new RotateDialogContext(document.PageCount, current, selected, _lastRotateDegrees);
        var result = await dialogs.ShowRotateDialogAsync(context);
        if (result == null)
            return;
        _lastRotateDegrees = result.Degrees;
        document.Rotate(result.Pages, result.Degrees);
    }

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void RotateLeft(int? page) => OpenDocument.Rotate(TargetPages(page), -90);

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void RotateRight(int? page) => OpenDocument.Rotate(TargetPages(page), 90);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeletePagesAsync(int? page)
    {
        if (page == null && DeleteSelectedStamp?.Invoke() == true)
            return; // Delete with a stamp selected removes the stamp, not pages
        var document = OpenDocument;
        var targets = TargetPages(page);
        if (targets.Length >= document.PageCount)
        {
            await dialogs.ShowErrorAsync(Loc.T("ErrDeleteAll"));
            return;
        }
        document.Delete(targets);
    }

    /// <summary>Ctrl/Cmd+Home: to the first page (both modes; navigating is fine while saving).</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void GoToFirstPage() => GoToPage(0);

    /// <summary>Ctrl/Cmd+End: to the last page (both modes).</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void GoToLastPage() => GoToPage(OpenDocument.PageCount - 1);

    /// <summary>File of the stamp picked in the gallery, waiting to be placed on a page; null = none.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelStampCommand))]
    private string? _pendingStampPath;

    /// <summary>Picks a stamp from the gallery of the ribbon.</summary>
    [RelayCommand]
    private void SelectStamp(string path)
    {
        if (Document == null)
            return;
        IsOrganizeMode = false; // stamps are placed in the reading view
        IsSelectTextToolActive = false;
        ActiveRedaction = null;
        IsCopyAreaToolActive = false;
        PendingPaste = null;
        PendingStampPath = path;
        ShowMessage(Loc.F("StampSelected", Path.GetFileNameWithoutExtension(path)));
    }

    /// <summary>
    /// Esc while a stamp or text waits to be placed: forget it. With the select-text tool Esc first removes the
    /// selection, then ends the tool.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasPendingPlacement))]
    private void CancelStamp()
    {
        if (IsSelectTextToolActive && SelectedText != null)
        {
            ClearTextSelectionRequested?.Invoke();
            return;
        }
        PendingStampPath = null;
        _pendingTemplate = null;
        IsTextToolActive = false;
        IsSelectTextToolActive = false;
        ActiveRedaction = null;
        IsCopyAreaToolActive = false;
        PendingPaste = null;
    }

    private bool HasPendingStamp => PendingStampPath != null;

    private bool HasPendingPlacement => PendingStampPath != null || IsTextToolActive || IsSelectTextToolActive || ActiveRedaction != null
        || IsCopyAreaToolActive || PendingPaste != null;

    #region Black out and white out

    /// <summary>
    /// The black-out or white-out tool (null = neither): dragging over text in the reading view covers those words,
    /// dragging elsewhere covers the area drawn. Stays on for more boxes until Esc or the button again.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelStampCommand))]
    [NotifyPropertyChangedFor(nameof(IsRedactToolActive))]
    [NotifyPropertyChangedFor(nameof(IsCoverToolActive))]
    private RedactionFill? _activeRedaction;

    public bool IsRedactToolActive => ActiveRedaction == RedactionFill.Black;

    public bool IsCoverToolActive => ActiveRedaction == RedactionFill.White;

    partial void OnActiveRedactionChanged(RedactionFill? value)
    {
        if (value == null)
            return;
        IsOrganizeMode = false; // boxes are drawn in the reading view
        PendingStampPath = null;
        _pendingTemplate = null;
        IsTextToolActive = false;
        IsSelectTextToolActive = false;
        IsCopyAreaToolActive = false;
        PendingPaste = null;
        ShowMessage(Loc.T(value == RedactionFill.Black ? "RedactToolSelected" : "CoverToolSelected"));
    }

    /// <summary>"Black out": covers the text selected with the select-text tool, otherwise switches the tool on or off.</summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void Redact() => UseRedaction(RedactionFill.Black);

    /// <summary>"White out": like <see cref="Redact"/>, in white.</summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void Cover() => UseRedaction(RedactionFill.White);

    private void UseRedaction(RedactionFill fill)
    {
        if (IsSelectTextToolActive && SelectedText != null)
        {
            RedactSelectionRequested?.Invoke(fill);
            return;
        }
        ActiveRedaction = ActiveRedaction == fill ? null : fill;
    }

    /// <summary>Black out or white out with selected text: the reading view covers the selection.</summary>
    public event Action<RedactionFill>? RedactSelectionRequested;

    /// <summary>
    /// Covers areas (one undo step): page index and the rect in the page as shown (points), several for text over more
    /// than one line or page.
    /// </summary>
    public void PlaceRedactions(IReadOnlyList<(int PageIndex, RectPt ViewRect)> areas, RedactionFill fill)
    {
        if (Document == null || IsBusy)
            return;
        var document = Document;
        var inPageSpace = areas
            .Where(a => a.PageIndex >= 0 && a.PageIndex < document.PageCount)
            .Select(a => (a.PageIndex, PageGeometry.Unrotate(a.ViewRect, document.Pages[a.PageIndex].BaseSize, document.Pages[a.PageIndex].ExtraRotation)))
            .ToList();
        if (document.AddRedactions(inPageSpace, fill).Count > 0)
            ShowMessage(Loc.T(fill == RedactionFill.Black ? "MsgRedacted" : "MsgCovered"));
    }

    /// <summary>A box was moved or resized (rectangle in page space); one undo step.</summary>
    public void ChangeRedaction(int redactionId, RectPt rect)
    {
        if (Document != null && !IsBusy)
            Document.UpdateRedaction(redactionId, rect);
    }

    #endregion

    #region Copy and paste areas

    /// <summary>The copied area, shared with the other windows (alone: this window's own).</summary>
    public AreaClipboard AreaClipboard { get; } = windows?.AreaClipboard ?? new AreaClipboard();

    /// <summary>The copy-area tool: a frame drawn around a part of a page in the reading view copies it as a picture.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelStampCommand))]
    private bool _isCopyAreaToolActive;

    /// <summary>The copied area waiting to be placed: a preview follows the pointer, a click places it; null = none.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelStampCommand))]
    private CopiedArea? _pendingPaste;

    partial void OnIsCopyAreaToolActiveChanged(bool value)
    {
        if (!value)
            return;
        IsOrganizeMode = false; // areas are copied in the reading view
        PendingStampPath = null;
        _pendingTemplate = null;
        IsTextToolActive = false;
        IsSelectTextToolActive = false;
        ActiveRedaction = null;
        PendingPaste = null;
        ShowMessage(Loc.T("CopyAreaToolSelected"));
    }

    partial void OnPendingPasteChanged(CopiedArea? value)
    {
        if (value != null)
            IsOrganizeMode = false; // pasted in the reading view
    }

    /// <summary>"Copy area": switches the tool on or off.</summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void ToggleCopyAreaTool() => IsCopyAreaToolActive = !IsCopyAreaToolActive;

    /// <summary>
    /// The reading view copied an area: it goes to the clipboard of all windows and is ready to be placed at once
    /// (Esc keeps it for later, Paste places it again).
    /// </summary>
    public void AreaCopied(CopiedArea area)
    {
        AreaClipboard.Current = area;
        IsCopyAreaToolActive = false;
        PasteAreaCommand.NotifyCanExecuteChanged();
        if (Document == null || IsBusy)
            return;
        PendingPaste = area;
        ShowMessage(Loc.T("MsgAreaCopied"));
    }

    private bool CanPasteArea() => CanUseDocument() && AreaClipboard.Current != null;

    /// <summary>"Paste" (Ctrl+V): the copied area waits to be placed with a click, in this or another window.</summary>
    [RelayCommand(CanExecute = nameof(CanPasteArea))]
    private void PasteArea()
    {
        if (AreaClipboard.Current is not { } area)
            return;
        PendingStampPath = null;
        _pendingTemplate = null;
        IsTextToolActive = false;
        IsSelectTextToolActive = false;
        ActiveRedaction = null;
        IsCopyAreaToolActive = false;
        PendingPaste = area;
        ShowMessage(Loc.T("PasteAreaSelected"));
    }

    /// <summary>
    /// Places the pending copied area where it was clicked (<paramref name="viewRect"/>: in the page as shown, points),
    /// as a stamp that covers what lies under it; that is removed from the file when saving. One undo step.
    /// </summary>
    public void PlacePastedArea(int pageIndex, RectPt viewRect)
    {
        if (PendingPaste is not { } area || Document == null || IsBusy || pageIndex < 0 || pageIndex >= Document.PageCount)
            return;
        var page = Document.Pages[pageIndex];
        var rect = PageGeometry.Unrotate(viewRect, page.BaseSize, page.ExtraRotation);
        Document.AddStamp(pageIndex, area.Png, area.PixelWidth, area.PixelHeight, rect, 360 - page.ExtraRotation, area.Bgra, coversContent: true);
        PendingPaste = null;
        ShowMessage(Loc.T("MsgAreaPasted"));
    }

    #endregion

    #region Selecting text

    /// <summary>
    /// The select-text tool: dragging over a page in the reading view selects its text (recognized words or the text
    /// layer of the file) instead of moving the content, so one sees which text is there and can copy it.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelStampCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllCommand))]
    private bool _isSelectTextToolActive;

    partial void OnIsSelectTextToolActiveChanged(bool value)
    {
        if (!value)
        {
            SetTextSelection(null);
            return;
        }
        IsOrganizeMode = false;
        PendingStampPath = null;
        _pendingTemplate = null;
        IsTextToolActive = false;
        ActiveRedaction = null;
        IsCopyAreaToolActive = false;
        PendingPaste = null;
        ShowMessage(Loc.T("SelectTextToolSelected"));
    }

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void ToggleSelectTextTool() => IsSelectTextToolActive = !IsSelectTextToolActive;

    /// <summary>The text selected with the select-text tool (null = nothing selected).</summary>
    public string? SelectedText { get; private set; }

    /// <summary>Called by the reading view when the selected text changes.</summary>
    public void SetTextSelection(string? text)
    {
        SelectedText = string.IsNullOrEmpty(text) ? null : text;
        OnPropertyChanged(nameof(SelectedText));
        CopyTextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The select-text tool hit a page without any text: neither recognized nor in the file.</summary>
    public void ReportPageWithoutText(int pageIndex) => ShowMessage(Loc.F("MsgPageHasNoText", pageIndex + 1));

    /// <summary>Select all with the select-text tool: the reading view selects the whole text of its page.</summary>
    public event Action? SelectAllTextRequested;

    /// <summary>Esc with selected text: the reading view removes the selection.</summary>
    public event Action? ClearTextSelectionRequested;

    #endregion

    #region Text style

    /// <summary>Pen colour of new text (#RRGGBB); remembered in the settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextBrush))]
    private string _textColor = TextStyle.NormalizeColor(settings.TextColor);

    [ObservableProperty]
    private string _textFont = TextStyle.NormalizeFont(settings.TextFont);

    [ObservableProperty]
    private double _textFontSize = TextStyle.ClampSize(settings.TextFontSize);

    /// <summary>The pen colour as brush (for the swatch in the ribbon).</summary>
    public Avalonia.Media.IBrush TextBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(TextColor));

    partial void OnTextColorChanged(string value)
    {
        var normalized = TextStyle.NormalizeColor(value);
        if (normalized != value)
            TextColor = normalized;
        else
            settings.TextColor = value;
    }

    partial void OnTextFontChanged(string value)
    {
        var normalized = TextStyle.NormalizeFont(value);
        if (normalized != value)
            TextFont = normalized;
        else
            settings.TextFont = value;
    }

    partial void OnTextFontSizeChanged(double value)
    {
        var clamped = TextStyle.ClampSize(value);
        if (clamped != value)
            TextFontSize = clamped;
        else
            settings.TextFontSize = value;
    }

    /// <summary>True while the text tool waits for a click on a page.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelStampCommand))]
    private bool _isTextToolActive;

    /// <summary>
    /// Places a typed text: <paramref name="viewRect"/> is the area it covers in the page as shown (points). Empty text places nothing.
    /// One undo step; the text tool is used up.
    /// </summary>
    public void PlaceText(int pageIndex, RectPt viewRect, string text)
    {
        _pendingTemplate = null;
        IsTextToolActive = false;
        text = text.TrimEnd();
        if (text.Length == 0 || Document == null || IsBusy || pageIndex < 0 || pageIndex >= Document.PageCount)
            return;
        var page = Document.Pages[pageIndex];
        var rect = PageGeometry.Unrotate(viewRect, page.BaseSize, page.ExtraRotation);
        Document.AddText(pageIndex, text, TextFont, TextFontSize, TextColor, rect, 360 - page.ExtraRotation);
        ShowMessage(Loc.T("TextPlaced"));
    }

    /// <summary>A placed text was edited: new content and area (as shown). Empty text removes it.</summary>
    public void ChangeText(int pageIndex, int textId, RectPt viewRect, string text)
    {
        if (Document == null || IsBusy || pageIndex < 0 || pageIndex >= Document.PageCount)
            return;
        text = text.TrimEnd();
        if (text.Length == 0)
        {
            Document.RemoveText(textId);
            return;
        }
        var page = Document.Pages[pageIndex];
        Document.UpdateText(textId, text, PageGeometry.Unrotate(viewRect, page.BaseSize, page.ExtraRotation));
    }

    /// <summary>Text of the template picked in the Templates menu, waiting to be placed (macros not expanded yet); null = none.</summary>
    private string? _pendingTemplate;

    /// <summary>The templates folder in use (the default while none is chosen).</summary>
    public string TemplateFolder => TemplateLibrary.ResolveFolder(settings);

    /// <summary>The templates of the folder, read anew each time; a missing folder is created (the samples come at program start, <see cref="SampleContent"/>).</summary>
    public IReadOnlyList<TextTemplate> LoadTemplates()
    {
        TemplateLibrary.TryEnsureFolder(TemplateFolder);
        return TemplateLibrary.Load(TemplateFolder);
    }

    /// <summary>Writes the templates edited in the Templates dialog into the folder; tells the user when a file could not be written.</summary>
    public async Task SaveTemplatesAsync(IEnumerable<TextTemplate> before, IReadOnlyList<TextTemplate> after)
    {
        if (!TemplateLibrary.Save(TemplateFolder, before, after))
            await dialogs.ShowErrorAsync(Loc.F("TemplatesSaveFailed", TemplateFolder), Loc.T("FolderProblemHint"));
    }

    /// <summary>Picks a template: the next click on a page places its text, with the macros expanded at that moment.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void SelectTemplate(TextTemplate template)
    {
        SelectTextTool();
        _pendingTemplate = template.Text;
        ShowMessage(Loc.F("TemplateSelected", template.Name));
    }

    /// <summary>The pending template with its macros expanded for a click on <paramref name="pageIndex"/>; null if none is pending.</summary>
    public string? ExpandPendingTemplate(int pageIndex)
    {
        if (_pendingTemplate is not { } text || Document == null)
            return null;
        return TemplateExpander.Expand(text, MacroContext(pageIndex));
    }

    /// <summary>What macros stand for on <paramref name="pageIndex"/> at this moment.</summary>
    public TemplateContext MacroContext(int pageIndex)
    {
        var name = Document?.FilePath is { } path ? Path.GetFileName(path) : Loc.T("Untitled");
        return new TemplateContext(DateTime.Now, Environment.UserName, name, pageIndex + 1, Document?.PageCount ?? 0);
    }

    /// <summary>The "Insert text" button: the next click on a page places a text box.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void SelectTextTool()
    {
        _pendingTemplate = null;
        IsOrganizeMode = false; // text is placed in the reading view
        IsSelectTextToolActive = false;
        ActiveRedaction = null;
        IsCopyAreaToolActive = false;
        PendingPaste = null;
        PendingStampPath = null;
        IsTextToolActive = true;
        ShowMessage(Loc.T("TextToolSelected"));
    }

    #endregion

    /// <summary>
    /// Places the pending stamp: <paramref name="viewRect"/> is where it was clicked, in the page as shown (points);
    /// the picture is kept in the document until it is saved. One undo step.
    /// </summary>
    public void PlaceStamp(int pageIndex, RectPt viewRect, byte[] image, int pixelWidth, int pixelHeight, byte[]? bgra = null, StampTemplate? template = null)
    {
        if (Document == null || IsBusy || pageIndex < 0 || pageIndex >= Document.PageCount)
            return;
        var page = Document.Pages[pageIndex];
        var rect = PageGeometry.Unrotate(viewRect, page.BaseSize, page.ExtraRotation);
        Document.AddStamp(pageIndex, image, pixelWidth, pixelHeight, rect, 360 - page.ExtraRotation, bgra, template: template);
        PendingStampPath = null;
        ShowMessage(template is { Definition.UsesDate: true } ? Loc.F("StampPlacedDated", template.Context.Now.ToString("d")) : Loc.T("StampPlaced"));
    }

    /// <summary>Tells that the stamp editor saved a stamp; the gallery reads the folder the next time it opens.</summary>
    public void ReportStampSaved(string name) => ShowMessage(Loc.F("StampSaved", name));

    /// <summary>
    /// A placed stamp with fields, made again with another date (one undo step); <paramref name="template"/> holds the new
    /// date. Returns the changed stamp, which has a new id.
    /// </summary>
    public StampOverlay? ChangeStampDate(int stampId, ComposedStamp picture, StampTemplate template)
    {
        if (Document == null || IsBusy)
            return null;
        var changed = Document.ReplaceStampPicture(stampId, picture.Png, picture.PixelWidth, picture.PixelHeight, picture.Bgra, template);
        if (changed != null)
            ShowMessage(Loc.F("StampDateChanged", template.Context.Now.ToString("d")));
        return changed;
    }

    /// <summary>Set by the window: deletes the selected placed stamp and returns true, or returns false if none is selected.</summary>
    public Func<bool>? DeleteSelectedStamp { get; set; }

    /// <summary>A placed stamp was moved or resized (rectangle in page space); one undo step.</summary>
    public void ChangeStamp(int stampId, RectPt rect)
    {
        if (Document != null && !IsBusy)
            Document.UpdateStamp(stampId, rect);
    }

    /// <summary>A placed text was moved or scaled (rectangle in page space); one undo step.</summary>
    public void ChangeTextBox(int textId, RectPt rect, double fontSize)
    {
        if (Document != null && !IsBusy)
            Document.UpdateText(textId, rect, fontSize);
    }

    /// <summary>Removes a placed stamp, text or box (ids are unique across all three) that is not saved yet; one undo step.</summary>
    public void RemoveStamp(int stampId)
    {
        if (Document != null && !IsBusy && !Document.RemoveStamp(stampId) && !Document.RemoveText(stampId))
            Document.RemoveRedaction(stampId);
    }

    /// <summary>Ctrl/Cmd+G: opens the box in the status bar to type a page number.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void ShowGoToPage() => GoToPageBoxRequested?.Invoke();

    /// <summary>Jumps to the page number typed into the "Go to page" box (1-based); out-of-range numbers go to the first/last page.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void GoToTypedPage(string? text)
    {
        if (Document == null || !int.TryParse(text?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var number))
            return;
        GoToPage(Math.Clamp(number, 1, Document.PageCount) - 1);
    }

    /// <summary>Shows a page in the current view (page sidebar, first/last page).</summary>
    public void GoToPage(int page)
    {
        if (Document == null || page < 0 || page >= Document.PageCount)
            return;
        CurrentPage = page;
        ScrollToPageRequested?.Invoke(page);
    }

    /// <summary>Drag and drop in Organize mode: moves the pages (one undo step); they stay selected.</summary>
    public void MovePages(IReadOnlyList<int> indices, int insertIndex)
    {
        if (Document == null || IsBusy)
            return;
        Document.Move(indices, insertIndex);
    }

    /// <summary>Organize mode: all pages; reading view with the select-text tool: the whole text of the page.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void SelectAll()
    {
        if (IsOrganizeMode)
            SetSelection([.. OpenDocument.Pages.Select(p => p.Id)]);
        else
            SelectAllTextRequested?.Invoke();
    }

    private bool CanSelectAll() => HasDocument && (IsOrganizeMode || IsSelectTextToolActive) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void ShowReading() => IsOrganizeMode = false;

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void ShowOrganize() => IsOrganizeMode = true;

    /// <summary>Toggles between Reading and Organize mode, optionally making a page the current one (context menu).</summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void SwitchMode(int? page)
    {
        if (page is >= 0 && page < OpenDocument.PageCount)
            CurrentPage = page.Value;
        IsOrganizeMode = !IsOrganizeMode;
    }

    #endregion

    #region Extract, split, concatenate

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private async Task ExtractPagesAsync(int? page)
    {
        var document = OpenDocument;
        var pages = TargetPages(page).Select(i => document.Pages[i]).ToList();
        var path = await dialogs.PickSavePathAsync(Loc.F("ExtractFileName", BaseNameOf(document)), FolderOf(document));
        if (path == null)
            return;

        BeginOperation(Loc.F("BusyExtracting", Loc.Pages(pages.Count)));
        try
        {
            await PdfSaver.ExtractAsync(pages, path, null, new Progress<double>(p => Progress = p), _operation.Token);
            ShowMessage(Loc.F("MsgExtracted", pages.Count, Path.GetFileName(path)));
        }
        catch (OperationCanceledException)
        {
            ShowMessage(Loc.T("MsgCancelled"));
        }
        catch (Exception ex)
        {
            await dialogs.ShowErrorAsync(Loc.T("ErrExtractFailed"), ex.Message);
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            IsBusy = false;
        }
    }

    /// <summary>Driver settings ("Properties…" in the print dialog) per printer, kept while the window is open.</summary>
    private readonly Dictionary<string, PrinterSetup> _printerSetups = [];

    /// <summary>Print: the dialog asks for printer, copies and pages; the pages (with all edits) go to a temporary PDF that is sent to the printer.</summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private async Task PrintAsync(int? page)
    {
        var document = OpenDocument;
        var service = printing ?? PlatformPrintService.Instance;
        var (current, selected) = DialogPages(page);
        var request = await dialogs.ShowPrintDialogAsync(new PrintDialogContext(
            document.PageCount, current, service.GetPrinters(), service.GetDefaultPrinter(),
            service, _printerSetups, selected));
        if (request == null)
            return;
        if (request.Setup != null)
            _printerSetups[request.Printer] = request.Setup;

        var pages = request.Pages.Where(i => i >= 0 && i < document.PageCount).Select(i => document.Pages[i]).ToList();
        if (pages.Count == 0)
            return;
        var temp = Path.Combine(AppInfo.TempFolder, "print-" + Guid.NewGuid().ToString("N") + ".pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        BeginOperation(Loc.F("BusyPrinting", pages.Count));
        try
        {
            await PdfSaver.ExtractAsync(pages, temp, null, new Progress<double>(p => Progress = p / 2), _operation.Token);
            await service.PrintAsync(temp, request.Printer, request.Copies, request.Setup, new Progress<double>(p => Progress = 0.5 + p / 2), _operation.Token);
            ShowMessage(Loc.F("MsgPrinted", pages.Count, request.Printer));
        }
        catch (OperationCanceledException)
        {
            ShowMessage(Loc.T("MsgCancelled"));
        }
        catch (Exception ex)
        {
            await dialogs.ShowErrorAsync(Loc.T("ErrPrintFailed"), ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // The spooler may still hold the file; it is in the temp folder anyway
            }
            _operation?.Dispose();
            _operation = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Send by email: a new message in the default email program with the document attached – the file itself if nothing
    /// is unsaved, otherwise a copy of what the window shows, under the document's name.
    /// The email program may hold the command until its message window closes, so it stays usable meanwhile.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument), AllowConcurrentExecutions = true)]
    private async Task SendByMailAsync()
    {
        var document = OpenDocument;
        var file = document.FilePath;
        if (file == null || document.IsDirty || !File.Exists(file))
        {
            file = await WriteMailCopyAsync(document);
            if (file == null)
                return;
        }
        ShowMessage(Loc.F("MsgMailOpened", Path.GetFileName(file)));
        try
        {
            await (mail ?? PlatformMailService.Instance).SendAsync(file, Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            await dialogs.ShowErrorAsync(Loc.T("ErrMailFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Writes the current state to a folder of its own in the temp folder (so the attachment keeps the document's name);
    /// null if it failed or was cancelled. The email program reads the file later, so it stays; copies from earlier days are removed.
    /// </summary>
    private async Task<string?> WriteMailCopyAsync(EditableDocument document)
    {
        var root = Path.Combine(AppInfo.TempFolder, "mail");
        DeleteOldMailCopies(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N"), BaseNameOf(document) + ".pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BeginOperation(Loc.T("BusyPreparingMail"));
        try
        {
            var (temp, _) = await PdfSaver.WriteDocumentAsync(document.Pages, document.BaseSource, path, new Progress<double>(p => Progress = p), _operation.Token);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException)
        {
            ShowMessage(Loc.T("MsgCancelled"));
            return null;
        }
        catch (Exception ex)
        {
            await dialogs.ShowErrorAsync(Loc.T("ErrMailFailed"), ex.Message);
            return null;
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            IsBusy = false;
        }
    }

    private static void DeleteOldMailCopies(string root)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open in the email program: next time
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private async Task SplitDocumentAsync()
    {
        var document = OpenDocument;
        var folder = FolderOf(document) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var selected = IsOrganizeMode ? SelectedIndices() : [];
        var request = await dialogs.ShowSplitDialogAsync(new SplitDialogContext(document.PageCount, selected, folder, BaseNameOf(document)));
        if (request == null)
            return;

        BeginOperation(Loc.F("BusySplitting", Loc.Files(request.Parts.Count)));
        try
        {
            var files = await PdfSaver.SplitAsync([.. document.Pages], request.Parts, request.Folder, request.BaseName, new Progress<double>(p => Progress = p), _operation.Token);
            ShowMessage(Loc.F("MsgSplitDone", Loc.Files(files.Count), request.Folder));
        }
        catch (OperationCanceledException)
        {
            ShowMessage(Loc.T("MsgCancelled"));
        }
        catch (Exception ex)
        {
            await dialogs.ShowErrorAsync(Loc.T("ErrSplitFailed"), ex.Message);
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            IsBusy = false;
        }
    }

    /// <summary>Builds a new, unsaved document from a list of files (optionally including the current document).</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task ConcatenateAsync() => ConcatenateFilesAsync([]);

    /// <summary>
    /// PDFs given on the command line: one is opened in Reading mode; several (or any number with <paramref name="merge"/>,
    /// the "Merge with &lt;app&gt;" context menu entry) open the Concatenate dialog with all of them listed in the
    /// given order, so the order can be adjusted before combining. Missing files are reported.
    /// </summary>
    public async Task OpenStartupFilesAsync(IReadOnlyList<string> args, bool merge = false)
    {
        var paths = args.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => Path.GetFullPath(a.Trim())).ToList();
        var missing = paths.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            await dialogs.ShowErrorAsync(Loc.T(missing.Count == 1 ? "ErrStartFileMissing" : "ErrStartFilesMissing"), string.Join(Environment.NewLine, missing));
        var files = paths.Except(missing).ToList();
        if (files.Count == 1 && !merge)
            await OpenPathAsync(files[0]);
        else if (files.Count > 0)
            await ConcatenateFilesAsync(files);
    }

    private async Task ConcatenateFilesAsync(IReadOnlyList<string> initialFiles)
    {
        var otherDocuments = windows == null ? null : (Func<IReadOnlyList<OpenDocumentInfo>>)(() => windows.OtherDocuments(this));
        var result = await dialogs.ShowConcatenateDialogAsync(new ConcatenateContext(HasDocument, initialFiles, otherDocuments));
        if (result == null)
            return;

        // Without the current document the result is a new document: in a new window if this one has a document
        var includesCurrent = result.Parts.Any(p => p == null);
        var inNewWindow = windows != null && HasDocument && !includesCurrent;
        if (!includesCurrent && !inNewWindow && !await ConfirmDiscardAsync())
        {
            foreach (var part in result.Parts)
                if (part != null)
                    await part.DisposeAsync();
            return;
        }

        var parts = result.Parts.Select(p => p ?? OpenDocument).ToList();
        var combined = EditableDocument.Combine(parts);
        if (windows != null && inNewWindow)
            await windows.OpenInNewWindowAsync(this, vm => vm.ShowCombinedAsync(combined, includesCurrent: false));
        else
            await ShowCombinedAsync(combined, includesCurrent);
    }

    private async Task ShowCombinedAsync(EditableDocument combined, bool includesCurrent)
    {
        var old = Document;
        _suggestedFileName = Loc.T("ConcatenatedFileName");
        Document = combined;
        CurrentPage = 0;
        SetSelection([]);
        if (!includesCurrent && old != null)
            await old.DisposeAsync();
        foreach (var source in combined.Sources)
            await LoadSizesAsync(source);
    }

    #endregion

    #region Text recognition (OCR)

    /// <summary>Loaded on first use (models, about 0.3 s) and kept for the session.</summary>
    private IOcrEngine? _ocrEngine;

    /// <summary>
    /// Recognizes the text of scanned pages (dialog: which pages, skip pages with text). The words are attached to the
    /// pages as one undo step and written as an invisible text layer when the document is saved.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private async Task RecognizeTextAsync(int? page)
    {
        var document = OpenDocument;
        var (current, selected) = DialogPages(page);
        var request = await dialogs.ShowOcrDialogAsync(new OcrDialogContext(document.PageCount, current, selected));
        if (request == null)
            return;
        var pages = request.Pages.Where(i => i >= 0 && i < document.PageCount).Select(i => document.Pages[i]).ToList();
        if (pages.Count == 0)
            return;

        BeginOperation(Loc.F("BusyOcr", 1, pages.Count));
        var token = _operation.Token;
        try
        {
            _ocrEngine ??= await Task.Run(ocrEngineFactory ?? (() => new RapidOcrEngine()), token);
            var progress = new Progress<int>(done =>
            {
                Progress = (double)done / pages.Count;
                if (done < pages.Count)
                    BusyText = Loc.F("BusyOcr", done + 1, pages.Count);
            });
            var found = await PageOcr.RecognizePagesAsync(_ocrEngine, pages, request.SkipPagesWithText, progress, token);
            if (!ReferenceEquals(Document, document))
                return;
            document.SetOcrWords(found);
            ShowMessage(found.Count == 0
                ? Loc.T("MsgOcrNothingToDo")
                : Loc.F("MsgOcrDone", Loc.Pages(found.Count), Loc.Words(found.Values.Sum(w => w.Count))));
        }
        catch (OperationCanceledException)
        {
            ShowMessage(Loc.T("MsgCancelled"));
        }
        catch (Exception ex)
        {
            await dialogs.ShowErrorAsync(Loc.T("ErrOcrFailed"), ex is FileNotFoundException missing ? $"{ex.Message} {missing.FileName}" : ex.Message);
        }
        finally
        {
            _operation?.Dispose();
            _operation = null;
            IsBusy = false;
            RefreshState();
        }
    }

    /// <summary>Copying text works on the pages selected in Organize mode, or on the text selected in the reading view.</summary>
    private bool CanCopyText() => CanUseDocument() && (IsOrganizeMode ? HasSelection : SelectedText != null);

    /// <summary>
    /// Copies the text of the selected pages to the clipboard: the recognized words (not saved yet) or the text layer of
    /// the file; in the reading view the text selected with the select-text tool. Nothing is shown, the status bar says
    /// what was copied.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopyText))]
    private async Task CopyTextAsync()
    {
        if (!IsOrganizeMode)
        {
            if (SelectedText is not { } selected)
                return;
            await dialogs.CopyToClipboardAsync(selected);
            ShowMessage(Loc.F("MsgSelectionCopied", Loc.Characters(selected.Length)));
            return;
        }
        var document = OpenDocument;
        var pages = SelectedIndices().Select(i => document.Pages[i]).ToList();
        try
        {
            var (text, pagesWithText) = await PageText.ReadPagesAsync(pages);
            if (pagesWithText == 0)
            {
                ShowMessage(Loc.T("MsgNoTextToCopy"));
                return;
            }
            await dialogs.CopyToClipboardAsync(text);
            ShowMessage(Loc.F("MsgTextCopied", pagesWithText, Loc.Characters(text.Length)));
        }
        catch (Exception ex)
        {
            await dialogs.ShowErrorAsync(Loc.T("ErrCopyTextFailed"), ex.Message);
        }
    }

    #endregion

    #region View

    /// <summary>The fit modes only exist for the reading view.</summary>
    private bool CanFit() => CanUseDocument() && !IsOrganizeMode;

    [RelayCommand(CanExecute = nameof(CanFit))]
    private void FitHeight() => ZoomMode = ZoomMode.FitHeight;

    [RelayCommand(CanExecute = nameof(CanFit))]
    private void FitWidth() => ZoomMode = ZoomMode.FitWidth;

    [RelayCommand(CanExecute = nameof(CanFit))]
    private void FitBest() => ZoomMode = ZoomMode.FitBest;

    /// <summary>
    /// Zoom the next step starts from: the set percentage (not the displayed one, which follows only after the next
    /// layout pass, so quick repeated clicks still take one step each), or the fitted zoom.
    /// </summary>
    private double StepBase => IsOrganizeMode ? ThumbnailZoomPercent : ZoomMode == ZoomMode.Custom ? CustomZoomPercent : EffectiveZoomPercent;

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void ZoomIn()
    {
        var steps = IsOrganizeMode ? _thumbnailZoomSteps : _zoomSteps;
        SetZoom(steps.FirstOrDefault(s => s > StepBase + 0.5, steps[^1]));
    }

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void ZoomOut()
    {
        var steps = IsOrganizeMode ? _thumbnailZoomSteps : _zoomSteps;
        SetZoom(steps.LastOrDefault(s => s < StepBase - 0.5, steps[0]));
    }

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void ActualSize() => SetZoom(100);

    /// <summary>A fixed zoom step (status bar menu); in Organize mode the thumbnail size.</summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void ZoomTo(double percent) => SetZoom(percent);

    /// <summary>Applies the percentage typed into the zoom box ("150" or "150 %").</summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private void ApplyZoomInput()
    {
        var text = ZoomInput?.Replace("%", "").Trim();
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var percent) && percent > 0)
            SetZoom(percent);
        else
            ZoomInput = ShownZoomPercent.ToString("0");
    }

    /// <summary>Sets the page zoom (Reading mode) or the thumbnail size (Organize mode), clamped to its range.</summary>
    private void SetZoom(double percent)
    {
        if (IsOrganizeMode)
        {
            ThumbnailZoomPercent = Math.Clamp(percent, ThumbnailGrid.MinZoomPercent, ThumbnailGrid.MaxZoomPercent);
            ZoomInput = ShownZoomPercent.ToString("0"); // shows the clamped value even if it did not change
            return;
        }
        CustomZoomPercent = HorizontalLayout.ClampZoom(percent);
        ZoomMode = ZoomMode.Custom;
    }

    [RelayCommand]
    private void ToggleRibbon() => IsRibbonCollapsed = !IsRibbonCollapsed;

    [RelayCommand]
    private void TogglePageSidebar() => IsPageSidebarVisible = !IsPageSidebarVisible;

    #endregion

    [RelayCommand]
    private Task ShowSettingsAsync() => dialogs.ShowSettingsAsync();

    [RelayCommand]
    private Task ShowAboutAsync() => dialogs.ShowAboutAsync();

    [RelayCommand]
    private Task ShowQuickGuideAsync() => dialogs.ShowQuickGuideAsync();

    /// <summary>The brand comes with an introduction video (button in the header, Help menu).</summary>
    public bool HasIntroVideo => IntroVideo.IsAvailable;

    [RelayCommand]
    private Task ShowIntroVideoAsync() => dialogs.ShowIntroVideoAsync();
}
