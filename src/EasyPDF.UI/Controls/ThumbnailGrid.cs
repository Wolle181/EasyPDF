using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EasyPDF.App.Styles;
using EasyPDF.Core.Caching;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.App.Controls;

/// <summary>
/// Organize mode: grid of page thumbnails with multi-selection and drag &amp; drop reordering.
/// With <see cref="IsNavigator"/> it is the page sidebar of the reading view instead: a click (or the arrow keys) asks to
/// show a page (<see cref="PageClicked"/>), and <see cref="ActivePage"/> is highlighted and kept in view.
/// Thumbnails are rendered lazily at low priority. The grid does not change the document itself; it raises
/// <see cref="SelectionRequested"/> and <see cref="MoveRequested"/> and the owner applies them.
/// </summary>
public sealed class ThumbnailGrid : Grid
{
    public const double MinZoomPercent = 50;
    public const double MaxZoomPercent = 300;
    private const double BaseBoxWidth = 150;
    private const double BaseBoxHeight = 190;
    private const double NavigatorMinBoxWidth = 40;
    private const double NavigatorBoxRatio = 1.42; // max. height : width of a navigator box, so upright A4 pages fill its width
    private const double NavigatorMinBoxRatio = 0.1; // very wide banners still get a box that can be clicked
    private const double CellPad = 12;
    private const double LabelHeight = 22;
    private const double DragStartDistance = 6;
    private const double WheelZoomStep = 1.1;
    private const long CacheBudgetBytes = 96L * 1024 * 1024;

    public static readonly StyledProperty<EditableDocument> DocumentProperty =
        AvaloniaProperty.Register<ThumbnailGrid, EditableDocument>(nameof(Document));

    /// <summary>Ids of the selected pages (<see cref="PageEntry.Id"/>); owned by the view model.</summary>
    public static readonly StyledProperty<IReadOnlyCollection<int>> SelectedIdsProperty =
        AvaloniaProperty.Register<ThumbnailGrid, IReadOnlyCollection<int>>(nameof(SelectedIds), []);

    /// <summary>The page with keyboard focus / last clicked (written by the grid).</summary>
    public static readonly StyledProperty<int> CurrentPageProperty =
        AvaloniaProperty.Register<ThumbnailGrid, int>(nameof(CurrentPage), 0, defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    public static readonly StyledProperty<int> ContextPageProperty =
        AvaloniaProperty.Register<ThumbnailGrid, int>(nameof(ContextPage), -1);

    /// <summary>
    /// Sidebar of the reading view: no selection, drag &amp; drop or zooming; clicks navigate. The thumbnails are in one
    /// column and fill its width, so a wider sidebar shows (and renders) them larger.
    /// </summary>
    public static readonly StyledProperty<bool> IsNavigatorProperty =
        AvaloniaProperty.Register<ThumbnailGrid, bool>(nameof(IsNavigator));

    /// <summary>Navigator: the page the reading view shows (-1 = none); highlighted and scrolled into view.</summary>
    public static readonly StyledProperty<int> ActivePageProperty =
        AvaloniaProperty.Register<ThumbnailGrid, int>(nameof(ActivePage), -1);

    /// <summary>Thumbnail size in percent of the standard size (100 = 150 × 190 px box); not used by the navigator.</summary>
    public static readonly StyledProperty<double> ZoomPercentProperty =
        AvaloniaProperty.Register<ThumbnailGrid, double>(nameof(ZoomPercent), 100, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay,
            coerce: (_, v) => Math.Clamp(v, MinZoomPercent, MaxZoomPercent));

    private sealed class Thumb : IDisposable
    {
        public WriteableBitmap? Bitmap; // null = rendering failed
        public int PixelWidth, PixelHeight, Rotation;
        public void Dispose() => Bitmap?.Dispose();
    }

    private sealed class Request
    {
        public required CancellationTokenSource Cts;
        public int PixelWidth, PixelHeight, Rotation;
    }

    private static readonly Color[] _sourceColors =
    [
        Color.FromRgb(0xE0, 0x7A, 0x1F), Color.FromRgb(0x2E, 0x9E, 0x6B), Color.FromRgb(0x8A, 0x4F, 0xD0),
        Color.FromRgb(0xD0, 0x3F, 0x6B), Color.FromRgb(0x2F, 0x8F, 0xCF), Color.FromRgb(0xA8, 0x9B, 0x1A)
    ];

    private readonly Surface _surface;
    private readonly ScrollBar _vBar = new() { Orientation = Avalonia.Layout.Orientation.Vertical, Visibility = ScrollBarVisibility.Visible, AllowAutoHide = false };
    private readonly PageImageCache<int, Thumb> _cache = new(CacheBudgetBytes, t => (long)t.PixelWidth * t.PixelHeight * 4);
    private readonly Dictionary<int, Request> _requests = new();
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(30) };

    private EditableDocument? _document;
    private double _scrollY;
    private bool _updatingBar;
    private int _generation;
    private int _anchor = -1;        // start for Shift selection
    private int _pendingSingleSelect = -1;
    private bool _dragCandidate;
    private bool _dragging;
    private Point _pressPoint;
    private Point _pointer;
    private int[] _dragIndices = [];
    private int _dropIndex = -1;
    private int _pendingScrollPage = -1;
    private double _autoScrollSpeed;

    public ThumbnailGrid()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _surface = new Surface(this);
        SetColumn(_surface, 0);
        SetColumn(_vBar, 1);
        Children.Add(_surface);
        Children.Add(_vBar);
        Focusable = true;

        _vBar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updatingBar)
                SetScroll(_vBar.Value);
        };
        _surface.PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty)
                return;
            if (IsNavigator && e.GetOldValue<Rect>().Width != e.GetNewValue<Rect>().Width && _pendingScrollPage < 0
                && ActivePage >= 0 && ActivePage < Pages.Count && Viewport.Width >= 1 && Viewport.Height >= 1)
                ScrollToPage(ActivePage); // the thumbnails changed size with the sidebar: keep the page being read in view
            else
                AfterLayoutChange();
        };
        _autoScroll.Tick += (_, _) =>
        {
            SetScroll(_scrollY + _autoScrollSpeed);
            UpdateDrop();
        };

        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerReleased += OnPointerReleased;
        _surface.PointerCaptureLost += (_, _) => CancelDrag();
        _surface.DoubleTapped += (_, e) =>
        {
            var index = PageIndexAt(e.GetPosition(_surface));
            if (index >= 0)
                PageActivated?.Invoke(index);
        };
        PointerWheelChanged += (_, e) =>
        {
            if (Primary(e.KeyModifiers) && !IsNavigator)
                ZoomPercent *= Math.Pow(WheelZoomStep, e.Delta.Y);
            else
                SetScroll(_scrollY - e.Delta.Y * 90);
            e.Handled = true;
        };
        KeyDown += OnKeyDown;
    }

    public EditableDocument Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }

    public IReadOnlyCollection<int> SelectedIds { get => GetValue(SelectedIdsProperty); set => SetValue(SelectedIdsProperty, value); }

    public int CurrentPage { get => GetValue(CurrentPageProperty); private set => SetValue(CurrentPageProperty, value); }

    public int ContextPage { get => GetValue(ContextPageProperty); private set => SetValue(ContextPageProperty, value); }

    public double ZoomPercent { get => GetValue(ZoomPercentProperty); set => SetValue(ZoomPercentProperty, value); }

    public bool IsNavigator { get => GetValue(IsNavigatorProperty); set => SetValue(IsNavigatorProperty, value); }

    public int ActivePage { get => GetValue(ActivePageProperty); set => SetValue(ActivePageProperty, value); }

    /// <summary>The user changed the selection (page ids).</summary>
    public event Action<IReadOnlyList<int>>? SelectionRequested;

    /// <summary>The user dropped pages: indices (ascending) and the insert position (0..PageCount).</summary>
    public event Action<IReadOnlyList<int>, int>? MoveRequested;

    /// <summary>Double-click on a page.</summary>
    public event Action<int>? PageActivated;

    /// <summary>Navigator: the user clicked a page or moved with the keyboard; the owner shows that page.</summary>
    public event Action<int>? PageClicked;

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    private Size Viewport => _surface.Bounds.Size;

    private IReadOnlyList<PageEntry> Pages => _document?.Pages ?? [];

    #region Layout

    private double BoxWidth => IsNavigator
        ? Math.Max(NavigatorMinBoxWidth, Viewport.Width - 2 * CellPad)
        : BaseBoxWidth * ZoomPercent / 100;

    /// <summary>Box height of the uniform Organize grid; in the sidebar the largest box (see <see cref="BoxHeightOf"/>).</summary>
    private double BoxHeight => IsNavigator ? BoxWidth * NavigatorBoxRatio : BaseBoxHeight * ZoomPercent / 100;

    /// <summary>
    /// Box height of one page: the Organize grid is uniform; in the sidebar every box follows its page's shape (up to
    /// upright A4), so landscape and unusual formats are not shown on an A4-sized card.
    /// </summary>
    private double BoxHeightOf(int index)
    {
        if (!IsNavigator)
            return BoxHeight;
        var size = Pages[index].DisplaySize;
        var ratio = size.Width > 0 && size.Height > 0 ? size.Height / size.Width : NavigatorBoxRatio;
        return Math.Round(BoxWidth * Math.Clamp(ratio, NavigatorMinBoxRatio, NavigatorBoxRatio));
    }

    private double CellWidth => BoxWidth + 2 * CellPad;

    /// <summary>Row height of the Organize grid; in the sidebar the tallest possible row.</summary>
    private double CellHeight => BoxHeight + LabelHeight + 2 * CellPad;

    // Sidebar rows have their own heights: tops as prefix sums, rebuilt when the pages or the width change
    private double[] _rowTops = [];
    private IReadOnlyList<PageEntry>? _rowTopsPages;
    private double _rowTopsWidth = -1;

    private void InvalidateRowTops() => _rowTopsPages = null;

    private double[] NavigatorRowTops()
    {
        var pages = Pages;
        if (!ReferenceEquals(_rowTopsPages, pages) || _rowTopsWidth != BoxWidth || _rowTops.Length != pages.Count + 1)
        {
            var tops = new double[pages.Count + 1];
            for (var i = 0; i < pages.Count; i++)
                tops[i + 1] = tops[i] + BoxHeightOf(i) + LabelHeight + 2 * CellPad;
            _rowTops = tops;
            _rowTopsPages = pages;
            _rowTopsWidth = BoxWidth;
        }
        return _rowTops;
    }

    /// <summary>Top of a row (0..Rows) in content coordinates, without the half padding above the first row.</summary>
    private double RowTop(int row) => IsNavigator ? NavigatorRowTops()[Math.Clamp(row, 0, Pages.Count)] : row * CellHeight;

    private double RowHeight(int row) => RowTop(row + 1) - RowTop(row);

    /// <summary>Row at content height <paramref name="y"/> (may be negative or ≥ Rows outside the rows).</summary>
    private int RowAt(double y)
    {
        if (!IsNavigator)
            return (int)Math.Floor(y / CellHeight);
        var tops = NavigatorRowTops();
        if (y < 0)
            return -1;
        if (y >= tops[^1])
            return Pages.Count;
        var index = Array.BinarySearch(tops, y);
        return index >= 0 ? index : ~index - 1;
    }

    private int Columns => Math.Max(1, (int)(Viewport.Width / CellWidth));

    private double OffsetX => Math.Max(0, (Viewport.Width - Columns * CellWidth) / 2);

    private int Rows => (Pages.Count + Columns - 1) / Columns;

    private double ContentHeight => RowTop(Rows) + CellPad;

    private Rect CellRect(int index)
    {
        var row = index / Columns;
        return new(OffsetX + index % Columns * CellWidth, RowTop(row) + CellPad / 2, CellWidth, RowHeight(row));
    }

    /// <summary>The page box inside its cell (the thumbnail fits into it, centred).</summary>
    private Rect ThumbRect(int index)
    {
        var cell = CellRect(index);
        var size = Pages[index].DisplaySize;
        var boxHeight = BoxHeightOf(index);
        var scale = Math.Min(BoxWidth / Math.Max(1, size.Width), boxHeight / Math.Max(1, size.Height));
        var w = Math.Max(1, Math.Floor(size.Width * scale));
        var h = Math.Max(1, Math.Floor(size.Height * scale));
        return new Rect(cell.X + CellPad + (BoxWidth - w) / 2, cell.Y + CellPad + (boxHeight - h) / 2, w, h);
    }

    /// <summary>
    /// The card around a page box (<see cref="ThumbRect"/>) with the page number below it: it takes the page's shape, so
    /// a page rotated to landscape (or any landscape page) lies on a landscape card, not on the portrait card of the
    /// uniform Organize cell. For an upright page that fills its box it is the whole cell less a small margin.
    /// </summary>
    private static Rect CardRect(Rect thumb)
    {
        const double margin = CellPad - 4;
        return new Rect(thumb.X - margin, thumb.Y - margin, thumb.Width + 2 * margin, thumb.Height + LabelHeight + 2 * margin);
    }

    private (int First, int Last) VisibleRange()
    {
        if (Pages.Count == 0)
            return (0, -1);
        var first = Math.Max(0, RowAt(_scrollY)) * Columns;
        var last = Math.Min(Pages.Count - 1, (RowAt(_scrollY + Viewport.Height) + 1) * Columns - 1);
        return (Math.Min(first, Pages.Count - 1), last);
    }

    public int PageIndexAt(Point p)
    {
        if (Pages.Count == 0)
            return -1;
        var col = (int)Math.Floor((p.X - OffsetX) / CellWidth);
        var row = RowAt(p.Y + _scrollY - CellPad / 2);
        if (col < 0 || col >= Columns || row < 0)
            return -1;
        var index = row * Columns + col;
        return index < Pages.Count ? index : -1;
    }

    /// <summary>Insert position (0..Count) for a drop at viewport position <paramref name="p"/>.</summary>
    private int InsertIndexAt(Point p)
    {
        var n = Pages.Count;
        if (n == 0)
            return 0;
        var row = RowAt(p.Y + _scrollY - CellPad / 2);
        if (row < 0)
            return 0;
        if (row >= Rows)
            return n;
        var col = (int)Math.Floor((p.X - OffsetX) / CellWidth);
        col = Math.Clamp(col, 0, Columns - 1);
        var index = Math.Min(row * Columns + col, n - 1);
        var cell = CellRect(index);
        var after = p.X - (cell.X) > CellWidth / 2;
        return Math.Min(n, index + (after ? 1 : 0));
    }

    private void AfterLayoutChange()
    {
        if (Viewport.Width < 1 || Viewport.Height < 1)
            return;
        if (_pendingScrollPage >= 0)
        {
            var page = _pendingScrollPage;
            _pendingScrollPage = -1;
            ScrollToPage(page);
            return;
        }
        SetScroll(_scrollY);
    }

    private void SetScroll(double y)
    {
        _scrollY = Math.Clamp(y, 0, Math.Max(0, ContentHeight - Viewport.Height));
        _updatingBar = true;
        try
        {
            _vBar.Minimum = 0;
            _vBar.Maximum = Math.Max(0, ContentHeight - Viewport.Height);
            _vBar.ViewportSize = Viewport.Height;
            _vBar.LargeChange = Math.Max(1, Viewport.Height * 0.8);
            _vBar.SmallChange = CellHeight / 3;
            _vBar.Value = _scrollY;
            _vBar.IsEnabled = _vBar.Maximum > 0;
        }
        finally
        {
            _updatingBar = false;
        }
        RequestRenders();
        _surface.InvalidateVisual();
    }

    /// <summary>Scrolls so that the page's row is visible (centred if it was not).</summary>
    public void ScrollToPage(int page)
    {
        if (Viewport.Width < 1 || Pages.Count == 0)
        {
            _pendingScrollPage = page;
            return;
        }
        page = Math.Clamp(page, 0, Pages.Count - 1);
        var cell = CellRect(page);
        if (cell.Top < _scrollY || cell.Bottom > _scrollY + Viewport.Height)
            SetScroll(cell.Center.Y - Viewport.Height / 2);
        else
            SetScroll(_scrollY);
        CurrentPage = page;
        _anchor = page;
    }

    #endregion

    #region Document handling

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            if (_document != null)
                _document.Changed -= OnDocumentChanged;
            _document = change.GetNewValue<EditableDocument>();
            InvalidateRowTops();
            if (_document != null)
                _document.Changed += OnDocumentChanged;
            _generation++;
            CancelRequests();
            _cache.Clear();
            _scrollY = 0;
            _anchor = -1;
            CurrentPage = 0;
            AfterLayoutChange();
        }
        else if (change.Property == ActivePageProperty)
        {
            // Follow the reading view: keep its page visible in the sidebar
            if (IsNavigator && ActivePage >= 0 && ActivePage < Pages.Count)
                ScrollToPage(ActivePage);
            _surface.InvalidateVisual();
        }
        else if (change.Property == SelectedIdsProperty)
        {
            _surface.InvalidateVisual();
        }
        else if (change.Property == ZoomPercentProperty && Viewport.Width >= 1)
        {
            // Keep the first page of the top row at the top; cached thumbnails are shown stretched until re-rendered
            var old = change.GetOldValue<double>() / 100;
            var oldColumns = Math.Max(1, (int)(Viewport.Width / (BaseBoxWidth * old + 2 * CellPad)));
            var top = (int)Math.Floor(_scrollY / (BaseBoxHeight * old + LabelHeight + 2 * CellPad)) * oldColumns;
            SetScroll(top < Pages.Count ? CellRect(top).Top - CellPad / 2 : _scrollY);
        }
    }

    private void OnDocumentChanged(DocumentChangeKind kind)
    {
        InvalidateRowTops();
        if (kind == DocumentChangeKind.Reloaded)
        {
            _generation++;
            CancelRequests();
            _cache.Clear();
        }
        AfterLayoutChange();
    }

    /// <summary>Call after more page sizes became known.</summary>
    public void RefreshPageSizes()
    {
        InvalidateRowTops();
        AfterLayoutChange();
    }

    private void CancelRequests()
    {
        foreach (var r in _requests.Values)
            r.Cts.Cancel();
        _requests.Clear();
    }

    #endregion

    #region Rendering

    private bool IsVisiblePage(int id)
    {
        var (first, last) = VisibleRange();
        var pages = Pages;
        for (var i = first; i <= last && i < pages.Count; i++)
            if (pages[i].Id == id)
                return true;
        return false;
    }

    private void RequestRenders()
    {
        var pages = Pages;
        if (pages.Count == 0 || Viewport.Width < 1)
            return;

        var (first, last) = VisibleRange();
        var prefetch = Columns;
        var from = Math.Max(0, first - prefetch);
        var to = Math.Min(pages.Count - 1, last + prefetch);
        var wantedIds = new HashSet<int>();
        for (var i = from; i <= to; i++)
            wantedIds.Add(pages[i].Id);

        foreach (var id in _requests.Keys.Where(id => !wantedIds.Contains(id)).ToList())
        {
            _requests[id].Cts.Cancel();
            _requests.Remove(id);
        }

        var scaling = Scaling;
        for (var i = from; i <= to; i++)
        {
            var entry = pages[i];
            var rect = ThumbRect(i);
            var pw = Math.Max(1, (int)Math.Round(rect.Width * scaling));
            var ph = Math.Max(1, (int)Math.Round(rect.Height * scaling));
            if (_cache.TryGet(entry.Id, out var cached) && cached.PixelWidth == pw && cached.PixelHeight == ph && cached.Rotation == entry.ExtraRotation)
                continue;
            if (_requests.TryGetValue(entry.Id, out var running))
            {
                if (running.PixelWidth == pw && running.PixelHeight == ph && running.Rotation == entry.ExtraRotation)
                    continue;
                running.Cts.Cancel();
            }
            var request = new Request { Cts = new CancellationTokenSource(), PixelWidth = pw, PixelHeight = ph, Rotation = entry.ExtraRotation };
            _requests[entry.Id] = request;
            _ = RenderAsync(entry, request, i >= first && i <= last ? PdfiumWorker.PriorityThumbnail : PdfiumWorker.PriorityThumbnail + 1, _generation);
        }
    }

    private async System.Threading.Tasks.Task RenderAsync(PageEntry entry, Request request, int priority, int generation)
    {
        RenderedPage? rendered = null;
        try
        {
            rendered = await entry.Source.RenderAsync(entry.SourceIndex, request.PixelWidth, request.PixelHeight, entry.ExtraRotation, priority, request.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // Broken page: keep the white placeholder (an empty entry stops endless retries)
        }

        if (generation != _generation || !_requests.TryGetValue(entry.Id, out var current) || current != request)
            return;
        _requests.Remove(entry.Id);
        var thumb = new Thumb { PixelWidth = request.PixelWidth, PixelHeight = request.PixelHeight, Rotation = entry.ExtraRotation };
        if (rendered != null)
            thumb.Bitmap = ToBitmap(rendered, Scaling);
        _cache.Set(entry.Id, thumb, IsVisiblePage);
        _surface.InvalidateVisual();
    }

    private static WriteableBitmap ToBitmap(RenderedPage page, double scaling)
    {
        var bitmap = new WriteableBitmap(new PixelSize(page.Width, page.Height), new Vector(96 * scaling, 96 * scaling),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        for (var y = 0; y < page.Height; y++)
            Marshal.Copy(page.Pixels, y * page.Stride, fb.Address + y * fb.RowBytes, Math.Min(page.Stride, fb.RowBytes));
        return bitmap;
    }

    #endregion

    #region Painting

    /// <summary>Colour from the theme resources (App.axaml), with a fallback for light and dark.</summary>
    private Color ThemeColor(string key, Color light, Color dark)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var v))
        {
            if (v is ISolidColorBrush brush)
                return brush.Color;
            if (v is Color c)
                return c;
        }
        return ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? dark : light;
    }

    private Color Accent() => ThemeColor("Selection.Background", BrandColors.Primary, BrandColors.Shade(BrandBase.Primary, 0.54, 0.40));

    private void Paint(DrawingContext context, Size size)
    {
        context.FillRectangle(new SolidColorBrush(ThemeColor("Organize.Background", Color.FromRgb(0xF3, 0xF4, 0xF6), Color.FromRgb(0x15, 0x1A, 0x22))), new Rect(size));
        var pages = Pages;
        if (_document == null || pages.Count == 0)
            return;

        // Every page sits on a card; the selected (or, in the sidebar, the active) card is filled in the selection colour
        var accent = Accent();
        var cardBrush = new SolidColorBrush(ThemeColor("Card.Background", Colors.White, Color.FromRgb(0x1E, 0x25, 0x30)));
        var cardPen = new Pen(new SolidColorBrush(ThemeColor("Card.Border", Color.FromRgb(0xDA, 0xDF, 0xE6), Color.FromRgb(0x2C, 0x36, 0x45))), 1);
        var selectedBrush = new SolidColorBrush(accent);
        var selected = new HashSet<int>(SelectedIds ?? []);
        var textBrush = new SolidColorBrush(ThemeColor("Text.Primary", Color.FromRgb(0x1C, 0x2B, 0x40), Color.FromRgb(0xE3, 0xE9, 0xF2)));
        var selectedTextBrush = new SolidColorBrush(ThemeColor("Selection.Foreground", Colors.White, Colors.White));
        var typeface = new Typeface(Avalonia.Controls.Documents.TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Medium);
        var (first, last) = VisibleRange();
        var sources = _document.Sources;

        for (var i = first; i <= last; i++)
        {
            var entry = pages[i];
            var cell = CellRect(i).Translate(new Vector(0, -_scrollY));
            if (cell.Bottom < 0 || cell.Top > size.Height)
                continue;

            var isSelected = IsNavigator ? i == ActivePage : selected.Contains(entry.Id);
            var thumb = ThumbRect(i).Translate(new Vector(0, -_scrollY));
            context.DrawRectangle(isSelected ? selectedBrush : cardBrush, isSelected ? null : cardPen, CardRect(thumb), 6, 6);

            context.FillRectangle(Brushes.White, thumb);
            if (_cache.TryGet(entry.Id, out var cached) && cached.Rotation == entry.ExtraRotation && cached.Bitmap != null)
                context.DrawWholeBitmap(cached.Bitmap, thumb);
            context.DrawRectangle(cardPen, thumb);

            // Pages that come from other files get a coloured marker below the page
            var sourceIndex = sources.ToList().IndexOf(entry.Source);
            if (!ReferenceEquals(entry.Source, _document.BaseSource) && sourceIndex >= 0)
            {
                var color = _sourceColors[sourceIndex % _sourceColors.Length];
                context.FillRectangle(new SolidColorBrush(color), new Rect(thumb.X, thumb.Bottom + 3, thumb.Width, 3));
            }

            var label = new FormattedText((i + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12, isSelected ? selectedTextBrush : textBrush);
            context.DrawText(label, new Point(thumb.X + (thumb.Width - label.Width) / 2, thumb.Bottom + 6));
        }

        if (_dragging)
            PaintDrag(context, accent);
    }

    private void PaintDrag(DrawingContext context, Color accent)
    {
        // Insertion marker
        if (_dropIndex >= 0 && Pages.Count > 0)
        {
            Rect cell;
            double x;
            if (_dropIndex < Pages.Count)
            {
                cell = CellRect(_dropIndex);
                x = cell.X + 2;
            }
            else
            {
                cell = CellRect(Pages.Count - 1);
                x = cell.Right - 2;
            }
            var top = cell.Y + CellPad - _scrollY;
            context.FillRectangle(new SolidColorBrush(accent), new Rect(x - 1.5, top, 3, BoxHeight));
        }

        // Ghost under the pointer
        var ghost = new Rect(_pointer.X - 30, _pointer.Y - 40, 60, 80);
        using (context.PushOpacity(0.75))
        {
            context.FillRectangle(Brushes.White, ghost);
            var index = _dragIndices.Length > 0 ? _dragIndices[0] : -1;
            if (index >= 0 && index < Pages.Count && _cache.TryGet(Pages[index].Id, out var t) && t.Bitmap != null)
                context.DrawWholeBitmap(t.Bitmap, ghost);
            context.DrawRectangle(new Pen(new SolidColorBrush(accent), 2), ghost);
        }
        if (_dragIndices.Length > 1)
        {
            var badge = new Rect(ghost.Right - 12, ghost.Top - 10, 24, 20);
            context.DrawRectangle(new SolidColorBrush(accent), null, badge, 10, 10);
            var text = new FormattedText(_dragIndices.Length.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
            context.DrawText(text, new Point(badge.X + (badge.Width - text.Width) / 2, badge.Y + 2));
        }
    }

    private sealed class Surface : Control
    {
        private readonly ThumbnailGrid _owner;

        public Surface(ThumbnailGrid owner)
        {
            _owner = owner;
            ClipToBounds = true;
            Focusable = false;
        }

        public override void Render(DrawingContext context) => _owner.Paint(context, Bounds.Size);
    }

    #endregion

    #region Selection and input

    private IReadOnlyList<int> SelectedIndices()
    {
        var ids = new HashSet<int>(SelectedIds ?? []);
        var list = new List<int>();
        for (var i = 0; i < Pages.Count; i++)
            if (ids.Contains(Pages[i].Id))
                list.Add(i);
        return list;
    }

    private void Select(IEnumerable<int> indices) =>
        SelectionRequested?.Invoke([.. indices.Where(i => i >= 0 && i < Pages.Count).Select(i => Pages[i].Id)]);

    private bool IsSelectedIndex(int index) => index >= 0 && index < Pages.Count && (SelectedIds ?? []).Contains(Pages[index].Id);

    private static bool Primary(KeyModifiers m) => m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(_surface);
        var position = point.Position;
        var index = PageIndexAt(position);

        if (IsNavigator)
        {
            if (point.Properties.IsLeftButtonPressed && index >= 0)
                PageClicked?.Invoke(index);
            return;
        }
        if (point.Properties.IsRightButtonPressed)
        {
            ContextPage = index;
            if (index >= 0 && !IsSelectedIndex(index))
                Select([index]);
            if (index >= 0)
                CurrentPage = index;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
            return;

        _pressPoint = position;
        _pointer = position;
        if (index < 0)
        {
            if (!Primary(e.KeyModifiers) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                Select([]);
            return;
        }

        CurrentPage = index;
        if (Primary(e.KeyModifiers))
        {
            var selection = SelectedIndices().ToList();
            if (!selection.Remove(index))
                selection.Add(index);
            Select(selection);
            _anchor = index;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var from = _anchor >= 0 ? _anchor : index;
            Select(Enumerable.Range(Math.Min(from, index), Math.Abs(index - from) + 1));
            return;
        }

        _anchor = index;
        if (IsSelectedIndex(index))
            _pendingSingleSelect = index;   // decided on release: a click selects only this page, a drag moves the selection
        else
            Select([index]);
        _dragCandidate = true;
        e.Pointer.Capture(_surface);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _pointer = e.GetPosition(_surface);
        if (_dragCandidate && !_dragging && Math.Sqrt(Math.Pow(_pointer.X - _pressPoint.X, 2) + Math.Pow(_pointer.Y - _pressPoint.Y, 2)) > DragStartDistance)
        {
            // Selection requests are applied synchronously by the owner, so the selection is current here
            _dragIndices = [.. SelectedIndices()];
            if (_dragIndices.Length > 0)
            {
                _dragging = true;
                _pendingSingleSelect = -1;
                _autoScroll.Start();
            }
        }
        if (_dragging)
        {
            UpdateDrop();
            var edge = 36.0;
            _autoScrollSpeed = _pointer.Y < edge ? -(edge - _pointer.Y) * 0.8 - 4
                : _pointer.Y > Viewport.Height - edge ? (_pointer.Y - (Viewport.Height - edge)) * 0.8 + 4 : 0;
        }
    }

    private void UpdateDrop()
    {
        _dropIndex = InsertIndexAt(_pointer);
        _surface.InvalidateVisual();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            var indices = _dragIndices;
            var drop = InsertIndexAt(e.GetPosition(_surface));
            CancelDrag();
            if (indices.Length > 0)
                MoveRequested?.Invoke(indices, drop);
        }
        else if (_pendingSingleSelect >= 0)
        {
            Select([_pendingSingleSelect]);
        }
        _pendingSingleSelect = -1;
        _dragCandidate = false;
        e.Pointer.Capture(null);
    }

    private void CancelDrag()
    {
        _dragging = false;
        _dragCandidate = false;
        _dropIndex = -1;
        _autoScroll.Stop();
        _autoScrollSpeed = 0;
        _surface.InvalidateVisual();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Primary(e.KeyModifiers) || Pages.Count == 0)
            return;
        var current = Math.Clamp(IsNavigator ? ActivePage : CurrentPage, 0, Pages.Count - 1);
        var target = e.Key switch
        {
            Key.Left => current - 1,
            Key.Right => current + 1,
            Key.Up => current - Columns,
            Key.Down => current + Columns,
            Key.Home => 0,
            Key.End => Pages.Count - 1,
            Key.PageUp => current - Columns * Math.Max(1, (int)(Viewport.Height / CellHeight)),
            Key.PageDown => current + Columns * Math.Max(1, (int)(Viewport.Height / CellHeight)),
            _ => int.MinValue,
        };
        if (target == int.MinValue)
            return;
        target = Math.Clamp(target, 0, Pages.Count - 1);
        if (IsNavigator)
        {
            PageClicked?.Invoke(target);
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var from = _anchor >= 0 ? _anchor : current;
            Select(Enumerable.Range(Math.Min(from, target), Math.Abs(target - from) + 1));
        }
        else
        {
            Select([target]);
            _anchor = target;
        }
        ScrollToPage(target);
        e.Handled = true;
    }

    #endregion
}
