using System;
using System.Linq;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Dialogs;

public partial class ConcatenateDialogView : DialogView
{
    private bool _accepted;

    public ConcatenateDialogView() => InitializeComponent();

    public ConcatenateDialogView(ConcatenateContext context, int currentPageCount) : this()
    {
        DataContext = new ConcatenateDialogViewModel(context, currentPageCount);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Reordering rows by dragging (files dropped from the file manager are handled by OnDrop)
        FileList.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        FileList.AddHandler(PointerMovedEvent, OnRowMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        FileList.AddHandler(PointerReleasedEvent, OnRowReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        FileList.PointerCaptureLost += (_, _) => EndRowDrag();
        if (context.InitialFiles is { Count: > 0 } files)
            Shown += async (_, _) => await Model.AddFilesAsync(files);
        // Cancelled (or closed with [X]): release the opened files; on OK the caller owns them
        Closed += async (_, _) =>
        {
            if (!_accepted)
                await Model.DisposeAllAsync();
        };
    }

    public ConcatenateDialogViewModel Model => (ConcatenateDialogViewModel)DataContext!;

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (Host is not { } host)
            return;
        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("ConcatAdd"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.T("FilterPdf")) { Patterns = ["*.pdf"], AppleUniformTypeIdentifiers =
                    ["com.adobe.pdf"], MimeTypes = ["application/pdf"]
                }
            ],
        });
        await Model.AddFilesAsync(await ToPathsAsync(files));
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true; // not also to the main view below (web version: the dialog is part of it)
        var files = await ToPathsAsync(e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>() ?? []);
        if (files.Count > 0)
            await Model.AddFilesAsync(files);
    }

    /// <summary>Paths for picked or dropped files (local paths; uploads in the web version).</summary>
    private static async System.Threading.Tasks.Task<System.Collections.Generic.List<string>> ToPathsAsync(System.Collections.Generic.IEnumerable<IStorageFile> files)
    {
        var paths = new System.Collections.Generic.List<string>();
        foreach (var file in files)
            if (await PlatformFeatures.Files.ToOpenPathAsync(file) is { } path)
                paths.Add(path);
        return paths;
    }

    #region Reordering by drag and drop

    private const double DragStartDistance = 6;
    private ConcatItem? _dragItem;
    private Point _pressPoint;
    private bool _dragging;

    /// <summary>The row being dragged (null if none), for tests and the drop marker.</summary>
    public bool IsDraggingRow => _dragging;

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
            return;
        _dragItem = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ConcatItem;
        _pressPoint = e.GetPosition(FileList);
        _dragging = false;
    }

    private void OnRowMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem == null)
            return;
        var position = e.GetPosition(FileList);
        if (!_dragging)
        {
            var dx = position.X - _pressPoint.X;
            var dy = position.Y - _pressPoint.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < DragStartDistance)
                return;
            _dragging = true;
            e.Pointer.Capture(FileList);
        }
        ShowDropMarker(DropIndexAt(position.Y));
    }

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _dragItem != null)
        {
            var index = DropIndexAt(e.GetPosition(FileList).Y);
            var item = _dragItem;
            EndRowDrag();
            Model.MoveTo(item, index);
        }
        else
        {
            EndRowDrag();
        }
        e.Pointer.Capture(null);
    }

    private void EndRowDrag()
    {
        _dragItem = null;
        _dragging = false;
        DropMarker.IsVisible = false;
    }

    /// <summary>Insert position (0..Count) for a pointer at <paramref name="y"/> (list coordinates).</summary>
    private int DropIndexAt(double y)
    {
        for (var i = 0; i < Model.Items.Count; i++)
        {
            if (FileList.ContainerFromIndex(i) is { } container &&
                container.TranslatePoint(new Point(0, container.Bounds.Height / 2), FileList) is { } center && y < center.Y)
                return i;
        }
        return Model.Items.Count;
    }

    private void ShowDropMarker(int index)
    {
        double y = 0;
        var count = Model.Items.Count;
        if (count > 0)
        {
            var at = Math.Min(index, count - 1);
            if (FileList.ContainerFromIndex(at) is { } container && container.TranslatePoint(new Point(0, 0), FileList) is { } top)
                y = index < count ? top.Y : top.Y + container.Bounds.Height;
        }
        DropMarker.Margin = new Thickness(0, Math.Max(0, y - 1.5), 0, 0);
        DropMarker.IsVisible = true;
    }

    #endregion

    private void OnRemove(object? sender, RoutedEventArgs e) => Model.Remove(Model.Selected);

    private void OnUp(object? sender, RoutedEventArgs e) => Model.MoveUp(Model.Selected);

    private void OnDown(object? sender, RoutedEventArgs e) => Model.MoveDown(Model.Selected);

    private void OnSort(object? sender, RoutedEventArgs e) => Model.SortByName();

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close(Model.BuildResult());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((ConcatenateResult?)null);
}

/// <summary>The dialog as a window (desktop).</summary>
public sealed class ConcatenateDialog : DialogWindow
{
    public ConcatenateDialog(ConcatenateContext context, int currentPageCount) : base(new ConcatenateDialogView(context, currentPageCount))
    {
        MinWidth = 520;
        MinHeight = 360;
    }

    public ConcatenateDialogView View => (ConcatenateDialogView)DialogContent;

    public ConcatenateDialogViewModel Model => View.Model;

    public bool IsDraggingRow => View.IsDraggingRow;
}
