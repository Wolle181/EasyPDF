using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;

namespace EasyPDF.App.Services;

/// <summary>
/// Keeps the main windows of the program (one document each, sharing one <see cref="AppSettings"/>) and decides which
/// window opens files that come from outside: the command line, a second start, macOS Finder.
/// </summary>
public sealed class WindowManager(AppSettings settings) : IDocumentWindows
{
    /// <summary>In activation order: the last one was active most recently.</summary>
    private readonly List<MainWindow> _windows = [];

    public IReadOnlyList<MainWindow> Windows => _windows;

    public int Count => _windows.Count;

    public AreaClipboard AreaClipboard { get; } = new();

    private static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static bool SamePath(string a, string b) => PathComparer.Equals(Path.GetFullPath(a), Path.GetFullPath(b));

    /// <summary>Creates a window (not shown yet); <paramref name="nextTo"/>: placed slightly offset from that window.</summary>
    public MainWindow CreateWindow(MainWindow? nextTo = null)
    {
        var window = new MainWindow(settings, this);
        if (nextTo != null)
            window.PlaceNextTo(nextTo);
        // Activation can be reported after the window closed: only reorder, never add it again
        window.Activated += (_, _) =>
        {
            if (_windows.Remove(window))
                _windows.Add(window);
        };
        window.Closed += (_, _) => _windows.Remove(window);
        _windows.Add(window);
        return window;
    }

    /// <summary>
    /// Empty windows that are being filled (or shown): requests arriving meanwhile, e.g. from several processes the
    /// file manager started at once, must not pick them as empty, or one document would replace the other.
    /// </summary>
    private readonly HashSet<MainWindow> _filling = [];

    private bool IsFree(MainWindow window) =>
        !window.ViewModel.HasDocument && !window.ViewModel.IsBusy && !_filling.Contains(window);

    /// <summary>Runs <paramref name="fill"/> on a window, which counts as taken until it is done.</summary>
    private async Task FillAsync(MainWindow window, Func<MainWindow, Task> fill)
    {
        var added = _filling.Add(window);
        try
        {
            await fill(window);
        }
        finally
        {
            if (added)
                _filling.Remove(window);
        }
    }

    /// <summary>Shows a new window and fills it once it is open (dialogs need a visible owner).</summary>
    private Task FillNewWindowAsync(MainWindow? nextTo, Func<MainWindow, Task> fill)
    {
        var window = CreateWindow(nextTo);
        var opened = new TaskCompletionSource();
        window.Opened += (_, _) => opened.TrySetResult();
        return FillAsync(window, async w =>
        {
            w.Show();
            await opened.Task;
            await fill(w);
        });
    }

    private MainWindow? WindowOf(MainViewModel viewModel) => _windows.FirstOrDefault(w => w.ViewModel == viewModel);

    private static void BringToFront(MainWindow window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void OpenNewWindow() => _ = FillNewWindowAsync(_windows.LastOrDefault(), _ => Task.CompletedTask);

    public bool ActivateWindowShowing(string path)
    {
        var window = _windows.FirstOrDefault(w => w.ViewModel.Document?.FilePath is { } file && SamePath(file, path));
        if (window == null)
            return false;
        BringToFront(window);
        return true;
    }

    public bool IsFileUsedByOtherWindow(string path, MainViewModel asking) =>
        _windows.Any(w => w.ViewModel != asking && w.ViewModel.Document is { } document && document.Sources.Any(s => SamePath(s.Path, path)));

    public IReadOnlyList<OpenDocumentInfo> OtherDocuments(MainViewModel asking) =>
    [
        .. Enumerable.Reverse(_windows)
            .Where(w => w.ViewModel != asking && !w.ViewModel.IsBusy)
            .Select(w => w.ViewModel.Document is { } document ? new OpenDocumentInfo(w.ViewModel.DocumentName, document) : null)
            .OfType<OpenDocumentInfo>(),
    ];

    public Task OpenInNewWindowAsync(MainViewModel from, Func<MainViewModel, Task> load) =>
        FillNewWindowAsync(WindowOf(from) ?? _windows.LastOrDefault(), w => load(w.ViewModel));

    public async Task CloseAllAsync()
    {
        // The active window first; each asks about its unsaved changes, Cancel keeps it and the rest open
        foreach (var window in Enumerable.Reverse(_windows.ToList()))
            if (!await window.CloseAfterConfirmAsync())
                return;
    }

    /// <summary>
    /// Files from outside (command line, second start, Finder): in the most recently active window without a document.
    /// If there is none, a single file starts from the most recently active window with a document (which opens it in a
    /// new window or shows the window that already has it, see <see cref="MainViewModel.OpenPathAsync"/>); the
    /// Concatenate dialog for several files (or --merge) and a request without files get a new window, so the dialog does
    /// not offer the active window's document. Windows being filled by an earlier request count as taken.
    /// </summary>
    public Task OpenFilesAsync(IReadOnlyList<string> files, bool merge)
    {
        async Task Open(MainWindow window)
        {
            BringToFront(window);
            if (files.Count > 0)
                await window.ViewModel.OpenStartupFilesAsync(files, merge);
        }

        var target = _windows.LastOrDefault(IsFree);
        if (target == null && files.Count == 1 && !merge)
            target = _windows.LastOrDefault(w => w.ViewModel.HasDocument);
        return target == null ? FillNewWindowAsync(_windows.LastOrDefault(), Open) : FillAsync(target, Open);
    }

    /// <summary>"Read" on several files (<c>--read</c>): each one in a window of its own, in the given order.</summary>
    public async Task OpenEachAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            await OpenFilesAsync([], merge: false);
        foreach (var file in files)
            await OpenFilesAsync([file], merge: false);
    }
}
