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
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Caching;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Layout;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.App.Controls;

/// <summary>
/// Reading view: pages side by side with horizontal scrolling. Only visible pages (plus a few neighbours) are
/// rendered, in the background on the pdfium worker; results are kept in an LRU cache with a byte budget.
/// The view does not own the document.
/// </summary>
public sealed partial class HorizontalPageView : Grid
{
    private const int PrefetchPages = 2;
    private const long CacheBudgetBytes = 256L * 1024 * 1024;
    private const double WheelZoomStep = 1.1;
    private const double PanStartDistance = 6;      // px the pointer must move before a press becomes a swipe
    private const double FlingSampleMs = 100;       // release velocity is measured over this last part of the swipe
    private const double FlingMinSpeed = 0.15;      // px/ms; slower releases stop at once
    private const double FlingMaxSpeed = 15;        // px/ms (a very fast swipe glides on for several pages)
    private const double FlingTimeConstantMs = 650; // speed decays as e^(-t/τ); the glide distance is speed × τ
    private const double FlingStopSpeed = 0.03;     // px/ms; below this the glide ends

    public static readonly StyledProperty<EditableDocument> DocumentProperty =
        AvaloniaProperty.Register<HorizontalPageView, EditableDocument>(nameof(Document));

    public static readonly StyledProperty<ZoomMode> ZoomModeProperty =
        AvaloniaProperty.Register<HorizontalPageView, ZoomMode>(nameof(ZoomMode), EasyPDF.Core.Layout.ZoomMode.FitHeight,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> CustomZoomPercentProperty =
        AvaloniaProperty.Register<HorizontalPageView, double>(nameof(CustomZoomPercent), 100,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Zero-based index of the page at the left edge (written by the view).</summary>
    public static readonly StyledProperty<int> CurrentPageProperty =
        AvaloniaProperty.Register<HorizontalPageView, int>(nameof(CurrentPage), 0,
            defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    /// <summary>Zoom of the current page in percent, 100 = real size (written by the view).</summary>
    public static readonly StyledProperty<double> EffectiveZoomPercentProperty =
        AvaloniaProperty.Register<HorizontalPageView, double>(nameof(EffectiveZoomPercent), 100,
            defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    /// <summary>Page under the pointer at the last right-click (-1 = none); context menu commands use it as parameter.</summary>
    /// <summary>The picture waiting to be placed (null = not placing): a preview follows the pointer, a click places it.</summary>
    public static readonly StyledProperty<Bitmap?> PendingStampImageProperty =
        AvaloniaProperty.Register<HorizontalPageView, Bitmap?>(nameof(PendingStampImage));

    /// <summary>True while the text tool waits for a click on a page: the click opens the text editor there.</summary>
    public static readonly StyledProperty<bool> IsTextToolActiveProperty =
        AvaloniaProperty.Register<HorizontalPageView, bool>(nameof(IsTextToolActive));

    public static readonly StyledProperty<int> ContextPageProperty =
        AvaloniaProperty.Register<HorizontalPageView, int>(nameof(ContextPage), -1);

    /// <summary>
    /// <see cref="PageEntry.Id"/> of the page marked with the red frame (-1 = none): the one clicked last, the target of the next edit.
    /// By id, so the mark moves with the page when pages are reordered.
    /// </summary>
    public static readonly StyledProperty<int> MarkedPageIdProperty =
        AvaloniaProperty.Register<HorizontalPageView, int>(nameof(MarkedPageId), -1,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private sealed class CachedPage : IDisposable
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

    private readonly PageSurface _surface;
    private readonly ScrollBar _hBar = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Visibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible, AllowAutoHide = false };
    private readonly ScrollBar _vBar = new() { Orientation = Avalonia.Layout.Orientation.Vertical, Visibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible, AllowAutoHide = false };
    private readonly HorizontalLayout _layout = new();
    private readonly PageImageCache<int, CachedPage> _cache = new(CacheBudgetBytes, p => (long)p.PixelWidth * p.PixelHeight * 4);
    private readonly Dictionary<int, Request> _requests = new();

    private EditableDocument? _document;
    private double _scrollX, _scrollY;
    private bool _updatingBars;
    private bool _internalZoomChange;
    private int _generation; // incremented when the document is replaced; late render results are dropped
    private (int First, int Last) _visible = (0, -1);

    // Swiping with the mouse: drag to pan, release with speed to let the content glide on
    private bool _panCandidate;
    private Point? _stampHover;
    private readonly Dictionary<int, Bitmap?> _stampBitmaps = [];
    private bool _panning;
    private Point _panStart;
    private double _panStartScrollX, _panStartScrollY;
    private readonly List<(long Ms, Point Position)> _panSamples = [];
    private static readonly System.Diagnostics.Stopwatch Stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private bool _flinging;
    private Vector _flingVelocity; // px/ms in scroll direction
    private long _flingLastMs;

    public HorizontalPageView()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions = new RowDefinitions("*,Auto");
        _surface = new PageSurface(this);
        SetColumn(_surface, 0);
        SetRow(_surface, 0);
        SetColumn(_vBar, 1);
        SetRow(_vBar, 0);
        SetColumn(_hBar, 0);
        SetRow(_hBar, 1);
        Children.Add(_surface);
        Children.Add(_vBar);
        Children.Add(_hBar);

        Focusable = true;
        _hBar.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && !_updatingBars) SetScroll(_hBar.Value, _scrollY); };
        _vBar.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && !_updatingBars) SetScroll(_scrollX, _vBar.Value); };
        _surface.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) OnViewportChanged(); };

        AddHandler(PointerPressedEvent, (_, e) =>
        {
            Focus();
            StopFling();
            var props = e.GetCurrentPoint(this).Properties;
            if (!props.IsRightButtonPressed && !props.IsLeftButtonPressed || !ReferenceEquals(e.Source, _surface))
                return; // scroll bars and other buttons leave the mark alone
            var position = e.GetPosition(_surface);
            if (props.IsRightButtonPressed && (PendingStampImage != null || IsTextToolActive || ActiveRedaction != null || IsCopyAreaToolActive))
            {
                StampCancelRequested?.Invoke();
                return;
            }
            if (props.IsLeftButtonPressed && IsTextToolActive)
            {
                BeginNewText(position);
                return;
            }
            if (props.IsLeftButtonPressed && ActiveRedaction != null)
            {
                SelectedStampId = -1;
                BeginRedaction(position, e.ClickCount);
                e.Pointer.Capture(_surface);
                return;
            }
            if (props.IsLeftButtonPressed && IsCopyAreaToolActive)
            {
                SelectedStampId = -1;
                BeginAreaCopy(position);
                e.Pointer.Capture(_surface);
                return;
            }
            if (props.IsLeftButtonPressed && IsSelectTextToolActive)
            {
                BeginTextSelection(position, e.ClickCount);
                e.Pointer.Capture(_surface);
                return;
            }
            if (props.IsLeftButtonPressed && e.ClickCount == 2 && (TryEditTextAt(position) || TryEditStampAt(position)))
                return;
            if (props.IsLeftButtonPressed && PendingStampImage == null)
            {
                if (TryBeginStampDrag(position))
                {
                    e.Pointer.Capture(_surface);
                    return;
                }
                SelectedStampId = -1;
            }
            if (props.IsRightButtonPressed)
            {
                ContextPage = PageIndexAt(position);
                MarkPageAt(position, toggle: false);
                return;
            }
            // Left button: a click marks the page, a drag swipes the content (decided when the pointer moves or is released)
            _panCandidate = true;
            _panning = false;
            _panStart = position;
            _panStartScrollX = _scrollX;
            _panStartScrollY = _scrollY;
            _panSamples.Clear();
            _panSamples.Add((Clock(), position));
            e.Pointer.Capture(_surface);
        }, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, e) => OnPanMoved(e), handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, e) => OnPanReleased(e), handledEventsToo: true);
        _surface.PointerCaptureLost += (_, _) => EndPan();

        MarkedPageIdProperty.Changed.AddClassHandler<HorizontalPageView>((v, _) => v._surface.InvalidateVisual());
        IsTextToolActiveProperty.Changed.AddClassHandler<HorizontalPageView>((v, _) =>
        {
            v._surface.Cursor = v.IsTextToolActive ? new Cursor(StandardCursorType.Ibeam) : v.PendingStampImage != null ? new Cursor(StandardCursorType.Cross) : null;
            if (!v.IsTextToolActive)
            {
                v._stampHover = null;
                v._surface.InvalidateVisual();
            }
        });
        ActiveRedactionProperty.Changed.AddClassHandler<HorizontalPageView>((v, _) =>
        {
            v.EndAreaDrag(commit: false);
            v._surface.Cursor = v.ActiveRedaction != null ? new Cursor(StandardCursorType.Cross) : null;
            if (v.ActiveRedaction != null)
            {
                v.SelectedStampId = -1;
                v.LoadVisibleWords();
            }
            else if (!v.IsSelectTextToolActive)
                v.ClearTextSelection();
        });
        IsCopyAreaToolActiveProperty.Changed.AddClassHandler<HorizontalPageView>((v, _) =>
        {
            v.EndAreaDrag(commit: false);
            v._surface.Cursor = v.IsCopyAreaToolActive ? new Cursor(StandardCursorType.Cross) : null;
            if (v.IsCopyAreaToolActive)
                v.SelectedStampId = -1;
        });
        PendingStampImageProperty.Changed.AddClassHandler<HorizontalPageView>((v, _) =>
        {
            v._surface.Cursor = v.PendingStampImage != null ? new Cursor(StandardCursorType.Cross) : null;
            v._surface.InvalidateVisual();
        });
        _surface.PointerExited += (_, _) =>
        {
            _stampHover = null;
            if (PendingStampImage != null || IsTextToolActive)
                _surface.InvalidateVisual();
        };
        PointerWheelChanged += OnWheel;
        KeyDown += OnKeyDown;
        Pinch += OnPinch;
        PinchEnded += (_, _) => _lastPinchScale = 1;
        GestureRecognizers.Add(new PinchGestureRecognizer());
        PointerTouchPadGestureMagnify += OnMagnify;
        InitTextSelection();
        InitSearch();
    }

    /// <summary>Asks to place the pending stamp: page index and the rectangle in the page as shown (points, upright in the view).</summary>
    public event Action<int, RectPt>? StampPlaceRequested;

    /// <summary>Escape or right click while placing: the user does not want to place the stamp after all.</summary>
    public event Action? StampCancelRequested;

    /// <summary>Double click on a placed stamp whose fields show a date: asks to change it.</summary>
    public event Action<int>? StampDateEditRequested;

    /// <summary>A placed stamp was moved or resized: stamp id and the new rectangle in page space.</summary>
    public event Action<int, RectPt>? StampChangeRequested;

    /// <summary>A placed text was moved or resized: text id, new rectangle in page space, new font size.</summary>
    public event Action<int, RectPt, double>? TextBoxChangeRequested;

    /// <summary>Delete was pressed with a stamp or text selected (ids are unique across both).</summary>
    public event Action<int>? StampDeleteRequested;

    /// <summary>Asks to delete the selected stamp; false if none is selected.</summary>
    public bool DeleteSelectedStamp()
    {
        if (SelectedStampId < 0 || _document == null)
            return false;
        StampDeleteRequested?.Invoke(SelectedStampId);
        SelectedStampId = -1;
        return true;
    }

    /// <summary>Id of the selected placed stamp (-1 = none).</summary>
    public int SelectedStampId
    {
        get => _selectedStampId;
        set
        {
            if (_selectedStampId == value)
                return;
            _selectedStampId = value;
            _surface.InvalidateVisual();
        }
    }

    private int _selectedStampId = -1;

    public Bitmap? PendingStampImage
    {
        get => GetValue(PendingStampImageProperty);
        set => SetValue(PendingStampImageProperty, value);
    }

    public EditableDocument Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public ZoomMode ZoomMode
    {
        get => GetValue(ZoomModeProperty);
        set => SetValue(ZoomModeProperty, value);
    }

    public double CustomZoomPercent
    {
        get => GetValue(CustomZoomPercentProperty);
        set => SetValue(CustomZoomPercentProperty, value);
    }

    public int ContextPage
    {
        get => GetValue(ContextPageProperty);
        private set => SetValue(ContextPageProperty, value);
    }

    public int MarkedPageId
    {
        get => GetValue(MarkedPageIdProperty);
        set => SetValue(MarkedPageIdProperty, value);
    }

    public int CurrentPage
    {
        get => GetValue(CurrentPageProperty);
        private set => SetValue(CurrentPageProperty, value);
    }

    public double EffectiveZoomPercent
    {
        get => GetValue(EffectiveZoomPercentProperty);
        private set => SetValue(EffectiveZoomPercentProperty, value);
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    private Size Viewport => _surface.Bounds.Size;

    #region Property changes

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
            AttachDocument(change.GetNewValue<EditableDocument>());
        else if ((change.Property == ZoomModeProperty || change.Property == CustomZoomPercentProperty) && !_internalZoomChange)
            ApplyExternalZoom();
    }

    private void AttachDocument(EditableDocument document)
    {
        if (_document != null)
            _document.Changed -= OnDocumentChanged;
        _document = document;
        if (_document != null)
            _document.Changed += OnDocumentChanged;
        ResetContent();
    }

    private void OnDocumentChanged(DocumentChangeKind kind)
    {
        if (kind == DocumentChangeKind.Reloaded)
        {
            // Same content in a new file (after saving): keep the reading position, but drop all renders
            _generation++;
            CancelAllRequests();
            _cache.Clear();
            ClearStampBitmaps();
            ForgetWords();
        }
        Relayout(keepPosition: true);
        ValidateTextSelection();
    }

    /// <summary>Index of the page at a viewport position, or -1 between/outside pages.</summary>
    public int PageIndexAt(Point viewportPoint)
    {
        if (_layout.Count == 0)
            return -1;
        var x = _scrollX + viewportPoint.X;
        var y = _scrollY + viewportPoint.Y;
        var page = _layout.FindPageAt(x);
        var b = _layout.PageBounds[page];
        return x <= b.Right && y >= b.Y && y <= b.Bottom ? page : -1;
    }

    /// <summary>Where a page is drawn, in viewport coordinates (may lie partly or fully outside the view).</summary>
    public Rect PageRectInViewport(int index)
    {
        var b = _layout.PageBounds[index];
        return new Rect(b.X - _scrollX, b.Y - _scrollY, b.Width, b.Height);
    }

    private void ResetContent()
    {
        CancelTextEditor();
        _generation++;
        CancelAllRequests();
        _cache.Clear();
        ClearStampBitmaps();
        ForgetWords();
        _scrollX = _scrollY = 0;
        Relayout(keepPosition: false);
    }

    /// <summary>Call after more page sizes became known (the layout was based on estimates).</summary>
    public void RefreshPageSizes() => Relayout(keepPosition: true);

    private void ApplyExternalZoom()
    {
        // Zoom changes coming from outside (ribbon, menu, ...) keep the viewport centre in place
        ZoomTo(ZoomMode, CustomZoomPercent, new Point(Viewport.Width / 2, Viewport.Height / 2));
    }

    private void OnViewportChanged() => Relayout(keepPosition: true);

    #endregion

    #region Layout

    private void Relayout(bool keepPosition)
    {
        var viewport = Viewport;
        var pages = _document?.Pages;
        if (pages != null && pages.Count > 0 && (viewport.Width < 1 || viewport.Height < 1))
            return; // hidden (e.g. Organize mode is shown): keep the layout and position for later
        if (pages == null || pages.Count == 0)
        {
            _layout.Update([], Math.Max(1, viewport.Width), Math.Max(1, viewport.Height), ZoomMode, CustomZoomPercent);
            UpdateScrollBars();
            PublishState();
            _surface.InvalidateVisual();
            return;
        }

        var (anchorPage, anchorFraction) = keepPosition ? _layout.GetAnchor(_scrollX + HorizontalLayout.OuterMargin) : (0, 0);
        _layout.Update([.. pages.Select(p => p.DisplaySize)], viewport.Width, viewport.Height, ZoomMode, CustomZoomPercent);
        if (keepPosition && _layout.Count > 0)
            _scrollX = _layout.GetXForAnchor(anchorPage, anchorFraction) - HorizontalLayout.OuterMargin;
        ClampScroll();
        AfterScrollOrLayout();
        if (_pendingScrollPage >= 0)
        {
            var page = _pendingScrollPage;
            _pendingScrollPage = -1;
            ScrollToPage(page);
        }
        ShowPendingSearchHit();
    }

    private int _pendingScrollPage = -1;

    private void ClampScroll()
    {
        _scrollX = Math.Clamp(_scrollX, 0, _layout.MaxScrollX(Viewport.Width));
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, _layout.ContentHeight - Viewport.Height));
    }

    private void SetScroll(double x, double y)
    {
        CommitTextEditor(); // the editor is anchored to the page; leaving it floating would be wrong
        _scrollX = x;
        _scrollY = y;
        ClampScroll();
        AfterScrollOrLayout();
    }

    private void AfterScrollOrLayout()
    {
        UpdateScrollBars();
        PublishState();
        RequestRenders();
        _surface.InvalidateVisual();
    }

    private void UpdateScrollBars()
    {
        _updatingBars = true;
        try
        {
            var v = Viewport;
            _hBar.Minimum = 0;
            _hBar.Maximum = _layout.MaxScrollX(v.Width);
            _hBar.ViewportSize = v.Width;
            _hBar.LargeChange = Math.Max(1, v.Width * 0.8);
            _hBar.SmallChange = Math.Max(20, v.Width / 10);
            _hBar.Value = _scrollX;
            _hBar.IsEnabled = _hBar.Maximum > 0;

            var maxY = Math.Max(0, _layout.ContentHeight - v.Height);
            _vBar.Minimum = 0;
            _vBar.Maximum = maxY;
            _vBar.ViewportSize = v.Height;
            _vBar.LargeChange = Math.Max(1, v.Height * 0.8);
            _vBar.SmallChange = Math.Max(20, v.Height / 10);
            _vBar.Value = _scrollY;
            _vBar.IsEnabled = maxY > 0;
        }
        finally
        {
            _updatingBars = false;
        }
    }

    private void PublishState()
    {
        if (!IsEffectivelyVisible || Viewport.Width < 1)
            return; // a hidden view must not overwrite the page chosen in the other mode
        var page = _layout.Count == 0 ? 0 : Math.Max(0, _layout.FindPageAt(_scrollX + HorizontalLayout.OuterMargin));
        CurrentPage = page;
        EffectiveZoomPercent = Math.Round(_layout.ZoomPercentOf(page));
    }

    public void ScrollToPage(int page)
    {
        if (_layout.Count == 0 || Viewport.Width < 1)
        {
            _pendingScrollPage = page;
            return;
        }
        SetScroll(_layout.ScrollXForPage(Math.Clamp(page, 0, _layout.Count - 1)), _scrollY);
    }

    #endregion

    #region Zoom

    /// <summary>Applies a zoom mode and keeps the content under <paramref name="focus"/> (viewport coordinates) in place.</summary>
    private void ZoomTo(ZoomMode mode, double percent, Point focus)
    {
        var (page, fx) = _layout.GetAnchor(_scrollX + focus.X);
        double fy = 0;
        if (_layout.Count > 0)
        {
            var b = _layout.PageBounds[page];
            fy = b.Height > 0 ? (_scrollY + focus.Y - b.Y) / b.Height : 0;
        }

        _internalZoomChange = true;
        try
        {
            ZoomMode = mode;
            CustomZoomPercent = HorizontalLayout.ClampZoom(percent);
        }
        finally
        {
            _internalZoomChange = false;
        }

        var pages = _document?.Pages;
        if (pages == null || pages.Count == 0)
            return;
        _layout.Update([.. pages.Select(p => p.DisplaySize)], Viewport.Width, Viewport.Height, ZoomMode, CustomZoomPercent);
        if (_layout.Count > 0)
        {
            var b = _layout.PageBounds[Math.Min(page, _layout.Count - 1)];
            _scrollX = _layout.GetXForAnchor(page, fx) - focus.X;
            _scrollY = b.Y + fy * b.Height - focus.Y;
        }
        ClampScroll();
        AfterScrollOrLayout();
    }

    /// <summary>Zooms by a factor relative to the zoom of the page under <paramref name="focus"/>.</summary>
    private void ZoomByFactor(double factor, Point focus)
    {
        var (page, _) = _layout.GetAnchor(_scrollX + focus.X);
        var current = _layout.Count == 0 ? CustomZoomPercent : _layout.ZoomPercentOf(page);
        ZoomTo(EasyPDF.Core.Layout.ZoomMode.Custom, current * factor, focus);
    }

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        if (e.Scale > 0)
            ZoomByFactor(e.Scale / Math.Max(0.01, _lastPinchScale), new Point(e.ScaleOrigin.X * Viewport.Width, e.ScaleOrigin.Y * Viewport.Height));
        _lastPinchScale = e.Scale;
        if (e.Handled == false)
            e.Handled = true;
    }

    private double _lastPinchScale = 1;

    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        // Trackpad pinch (macOS); Delta.X is the relative scale change
        ZoomByFactor(1 + e.Delta.X, e.GetPosition(_surface));
        e.Handled = true;
    }

    #endregion

    #region Input

    private double ScrollStep => Math.Max(60, Viewport.Width / 5);

    #region Swiping with the mouse

    /// <summary>True while the content follows a dragging mouse.</summary>
    public bool IsPanning => _panning;

    /// <summary>True while the content glides on after a swipe.</summary>
    public bool IsFlinging => _flinging;

    /// <summary>Speed the content glides with right now, in pixels per millisecond (0 when it does not glide).</summary>
    public double FlingSpeed => _flinging ? Math.Sqrt(_flingVelocity.X * _flingVelocity.X + _flingVelocity.Y * _flingVelocity.Y) : 0;

    /// <summary>
    /// Milliseconds the swipe speed and the glide are measured with. Tests set their own clock to swipe at an exact
    /// speed (real delays vary with the timer resolution and the load of the machine).
    /// </summary>
    public Func<long> Clock { get; set; } = () => Stopwatch.ElapsedMilliseconds;

    /// <summary>Current scroll position in pixels (left/top edge of the viewport in the content).</summary>
    public Vector ScrollOffset => new(_scrollX, _scrollY);

    /// <summary>Marks the page at the position with the red frame; with <paramref name="toggle"/> the marked page is unmarked.</summary>
    private void MarkPageAt(Point position, bool toggle)
    {
        // The red frame shows which page the next edit applies to; left-clicking the marked page again removes it
        var index = PageIndexAt(position);
        var id = index >= 0 && _document != null && index < _document.PageCount ? _document.Pages[index].Id : -1;
        MarkedPageId = toggle && id == MarkedPageId ? -1 : id;
    }

    private void OnPanMoved(PointerEventArgs e)
    {
        if (PendingStampImage != null && ReferenceEquals(e.Source, _surface))
        {
            _stampHover = e.GetPosition(_surface);
            // Over a page the picture takes the place of the pointer; a swipe keeps its own pointer
            if (!_panning)
                _surface.Cursor = new Cursor(StampTargetAt(e.GetPosition(_surface)) != null ? StandardCursorType.None : StandardCursorType.Cross);
            _surface.InvalidateVisual();
        }
        if (IsTextToolActive && ReferenceEquals(e.Source, _surface))
            UpdateTemplatePreview(e.GetPosition(_surface));
        if (_stampDrag != null)
        {
            UpdateStampDrag(e.GetPosition(_surface));
            e.Handled = true;
            return;
        }
        if (_areaDrag != null)
        {
            UpdateAreaDrag(e.GetPosition(_surface));
            e.Handled = true;
            return;
        }
        if (_selectingText)
        {
            UpdateTextSelection(e.GetPosition(_surface));
            e.Handled = true;
            return;
        }
        if (IsSelectTextToolActive && ReferenceEquals(e.Source, _surface))
            UpdateTextCursor(e.GetPosition(_surface));
        else if (ActiveRedaction != null && ReferenceEquals(e.Source, _surface))
            UpdateRedactionCursor(e.GetPosition(_surface));
        if (!_panCandidate)
            return;
        var position = e.GetPosition(_surface);
        var delta = position - _panStart;
        if (!_panning && Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) < PanStartDistance)
            return;
        if (!_panning)
        {
            _panning = true;
            _surface.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        // The content follows the pointer: dragging to the left shows what is further right
        SetScroll(_panStartScrollX - delta.X, _panStartScrollY - delta.Y);
        var now = Clock();
        _panSamples.Add((now, position));
        _panSamples.RemoveAll(sample => now - sample.Ms > FlingSampleMs * 2);
        e.Handled = true;
    }

    private void OnPanReleased(PointerReleasedEventArgs e)
    {
        if (_stampDrag != null)
        {
            EndStampDrag(commit: true);
            e.Pointer.Capture(null);
            return;
        }
        if (_areaDrag != null)
        {
            EndAreaDrag(commit: true);
            e.Pointer.Capture(null);
            return;
        }
        if (_selectingText)
        {
            if (_redactingText)
                EndRedactionText();
            else
                EndTextSelection();
            e.Pointer.Capture(null);
            return;
        }
        if (!_panCandidate)
            return;
        var position = e.GetPosition(_surface);
        if (_panning)
            StartFling(position);
        else if (PendingStampImage != null)
            RequestStampPlacement(position);
        else
            MarkPageAt(position, toggle: true);
        EndPan();
        e.Pointer.Capture(null);
    }

    private void EndPan()
    {
        if (_stampDrag != null)
            EndStampDrag(commit: false);
        EndAreaDrag(commit: false);
        _redactingText = false;
        EndTextSelection();
        _panCandidate = false;
        _panning = false;
        _surface.Cursor = null;
    }

    /// <summary>Lets the content glide on with the speed of the last part of the swipe.</summary>
    private void StartFling(Point releasePosition)
    {
        var now = Clock();
        _panSamples.Add((now, releasePosition));
        var recent = _panSamples.Where(sample => now - sample.Ms <= FlingSampleMs).ToList();
        if (recent.Count < 2)
            return; // the pointer rested before release: stop here
        var first = recent[0];
        var elapsed = Math.Max(8, now - first.Ms);
        var velocity = (first.Position - releasePosition) / elapsed; // scrolling goes against the pointer movement
        var speed = Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        if (speed < FlingMinSpeed)
            return;
        if (speed > FlingMaxSpeed)
            velocity *= FlingMaxSpeed / speed;
        _flingVelocity = velocity;
        _flingLastMs = now;
        _flinging = true;
        RequestFlingFrame();
    }

    /// <summary>The glide advances once per displayed frame (in sync with the screen, so it looks smooth).</summary>
    private void RequestFlingFrame()
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(_ => FlingStep());
        else
            StopFling();
    }

    private void FlingStep()
    {
        if (!_flinging)
            return;
        var now = Clock();
        var dt = Math.Clamp(now - _flingLastMs, 0, 100);
        _flingLastMs = now;
        // Exact integral of the exponentially decaying speed over dt, independent of the frame rate
        var decay = Math.Exp(-dt / FlingTimeConstantMs);
        var distance = _flingVelocity * (FlingTimeConstantMs * (1 - decay));
        _flingVelocity *= decay;
        var (oldX, oldY) = (_scrollX, _scrollY);
        SetScroll(_scrollX + distance.X, _scrollY + distance.Y);
        var stuck = dt > 0 && Math.Abs(_scrollX - oldX) < 0.01 && Math.Abs(_scrollY - oldY) < 0.01; // reached the first or last page
        if (FlingSpeed < FlingStopSpeed || stuck)
            StopFling();
        else
            RequestFlingFrame();
    }

    private void StopFling()
    {
        _flinging = false;
        _flingVelocity = default;
    }

    #endregion

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        StopFling();
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl)
        {
            ZoomByFactor(Math.Pow(WheelZoomStep, e.Delta.Y), e.GetPosition(_surface));
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            SetScroll(_scrollX, _scrollY - d * ScrollStep);
        }
        else
        {
            // Mouse wheel scrolls horizontally; trackpads deliver real X/Y deltas
            SetScroll(_scrollX - (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * ScrollStep, _scrollY - (e.Delta.X != 0 ? e.Delta.Y * ScrollStep : 0));
        }
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        StopFling();
        if (e.Key is Key.Delete or Key.Back && DeleteSelectedStamp())
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SelectedStampId >= 0 && PendingStampImage == null)
        {
            SelectedStampId = -1;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && (PendingStampImage != null || IsTextToolActive || ActiveRedaction != null || IsCopyAreaToolActive))
        {
            StampCancelRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            return; // shortcuts are handled by the window
        switch (e.Key)
        {
            case Key.Left: SetScroll(_scrollX - ScrollStep, _scrollY); break;
            case Key.Right: SetScroll(_scrollX + ScrollStep, _scrollY); break;
            case Key.Up:
            case Key.PageUp: PreviousPage(); break;
            case Key.Down:
            case Key.PageDown: ScrollToPage(CurrentPage + 1); break;
            case Key.Home: SetScroll(0, _scrollY); break;
            case Key.End: SetScroll(double.MaxValue, _scrollY); break;
            default: return;
        }
        e.Handled = true;
    }

    private void PreviousPage()
    {
        // First go back to the start of the current page, then to the previous one
        var page = CurrentPage;
        ScrollToPage(_scrollX > _layout.ScrollXForPage(page) + 1 ? page : page - 1);
    }

    #endregion

    #region Rendering

    private bool IsPageVisible(int pageId, IReadOnlyList<PageEntry> pages)
    {
        for (var i = _visible.First; i <= _visible.Last && i < pages.Count; i++)
            if (pages[i].Id == pageId)
                return true;
        return false;
    }

    private void CancelAllRequests()
    {
        foreach (var r in _requests.Values)
            r.Cts.Cancel();
        _requests.Clear();
    }

    /// <summary>Starts renders for visible pages and a few neighbours, and cancels everything else.</summary>
    private void RequestRenders()
    {
        var pages = _document?.Pages;
        if (pages == null || _layout.Count == 0 || _layout.Count != pages.Count)
            return;

        _visible = _layout.VisibleRange(_scrollX, Viewport.Width);
        var wanted = new List<(int Index, int Priority)>();
        for (var i = _visible.First; i <= _visible.Last; i++)
            wanted.Add((i, PdfiumWorker.PriorityVisible));
        for (var d = 1; d <= PrefetchPages; d++)
        {
            if (_visible.Last + d < pages.Count)
                wanted.Add((_visible.Last + d, PdfiumWorker.PriorityPrefetch));
            if (_visible.First - d >= 0)
                wanted.Add((_visible.First - d, PdfiumWorker.PriorityPrefetch));
        }

        var wantedIds = new HashSet<int>(wanted.Select(w => pages[w.Index].Id));
        foreach (var id in _requests.Keys.Where(id => !wantedIds.Contains(id)).ToList())
        {
            _requests[id].Cts.Cancel();
            _requests.Remove(id);
        }

        var scaling = Scaling;
        foreach (var (index, priority) in wanted)
        {
            var entry = pages[index];
            var b = _layout.PageBounds[index];
            var pw = Math.Max(1, (int)Math.Round(b.Width * scaling));
            var ph = Math.Max(1, (int)Math.Round(b.Height * scaling));

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
            _ = RenderAsync(entry, request, priority, _generation);
        }
        LoadVisibleWords();
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
            // Broken page: cached without bitmap (an empty entry stops endless retries)
        }

        if (generation != _generation || !_requests.TryGetValue(entry.Id, out var current) || current != request)
            return;
        _requests.Remove(entry.Id);

        var page = new CachedPage { PixelWidth = request.PixelWidth, PixelHeight = request.PixelHeight, Rotation = entry.ExtraRotation };
        if (rendered != null)
            page.Bitmap = ToBitmap(rendered, Scaling);
        var pages = _document?.Pages;
        _cache.Set(entry.Id, page, pages == null ? null : id => IsPageVisible(id, pages));
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

    /// <summary>Index of the page with the red frame (-1 = none).</summary>
    public int HighlightedPage
    {
        get
        {
            if (MarkedPageId < 0 || _document == null)
                return -1;
            for (var i = 0; i < _document.PageCount; i++)
                if (_document.Pages[i].Id == MarkedPageId)
                    return i;
            return -1;
        }
    }

    private void Paint(DrawingContext context, Size size)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        // Same colour as the start page behind the views (App.axaml)
        var background = this.TryFindResource("Content.Background", ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : new SolidColorBrush(dark ? Color.FromRgb(0x0F, 0x13, 0x1A) : Color.FromRgb(0xE6, 0xE9, 0xEE));
        context.FillRectangle(background, new Rect(size));

        var pages = _document?.Pages;
        if (pages == null || _layout.Count == 0 || _layout.Count != pages.Count)
            return;

        var (first, last) = _layout.VisibleRange(_scrollX, size.Width);
        var border = new Pen(this.TryFindResource("Page.Border", ActualThemeVariant, out var pageBorder) && pageBorder is IBrush pb ? pb : Brushes.DimGray, 1);
        // Soft shadow under each page, so white pages stand out from the light background
        var shadow = new SolidColorBrush(Colors.Black, dark ? 0.35 : 0.10);
        for (var i = first; i <= last; i++)
        {
            var b = _layout.PageBounds[i];
            var target = new Rect(b.X - _scrollX, b.Y - _scrollY, b.Width, b.Height);
            if (target.Bottom < 0 || target.Top > size.Height)
                continue;

            context.FillRectangle(shadow, target.Translate(new Vector(0, 2)).Inflate(1));
            context.FillRectangle(Brushes.White, target);
            if (_cache.TryGet(pages[i].Id, out var cached) && cached.Rotation == pages[i].ExtraRotation)
            {
                if (cached.Bitmap != null)
                    context.DrawWholeBitmap(cached.Bitmap, target);
                else
                    DrawCentered(context, Loc.F("PageRenderFailed", i + 1), target, Brushes.Red);
            }
            else
            {
                DrawCentered(context, Loc.F("PageLoading", i + 1), target, Brushes.Gray);
            }
            DrawOverlays(context, pages[i], target);
            DrawSearchHits(context, pages[i], target);
            DrawTextSelection(context, i, pages[i], target);
            context.DrawRectangle(border, target);
            if (pages[i].Id == MarkedPageId)
                ContextHighlight.Draw(context, target);
        }
        DrawStampPreview(context, pages);
        DrawTemplatePreview(context);
        DrawAreaDraft(context);
    }

    #region Stamps

    /// <summary>Slightly see-through, so a picture without transparency doesn't hide where it is meant to go.</summary>
    private const double StampPreviewOpacity = 0.8;

    private Bitmap? StampBitmap(StampOverlay stamp)
    {
        if (_stampBitmaps.TryGetValue(stamp.Id, out var known))
            return known;
        Bitmap? bitmap = null;
        try
        {
            using var stream = new System.IO.MemoryStream(stamp.Image);
            bitmap = new Bitmap(stream);
        }
        catch (Exception)
        {
            // Unreadable picture: remembered as null so it is not decoded again on every frame
        }
        _stampBitmaps[stamp.Id] = bitmap;
        return bitmap;
    }

    private void ClearStampBitmaps()
    {
        foreach (var bitmap in _stampBitmaps.Values)
            bitmap?.Dispose();
        _stampBitmaps.Clear();
    }

    /// <summary>Draws a picture into the rectangle it covers in the view, turned by <paramref name="degrees"/> about the centre.</summary>
    private static void DrawStampImage(DrawingContext context, Bitmap bitmap, Rect covered, int degrees, double opacity)
    {
        var centre = covered.Center;
        var sideways = degrees is 90 or 270;
        var (w, h) = sideways ? (covered.Height, covered.Width) : (covered.Width, covered.Height);
        var transform = Matrix.CreateTranslation(-centre.X, -centre.Y) * Matrix.CreateRotation(degrees * Math.PI / 180) * Matrix.CreateTranslation(centre.X, centre.Y);
        using (context.PushTransform(transform))
        using (context.PushOpacity(opacity))
            context.DrawWholeBitmap(bitmap, new Rect(centre.X - w / 2, centre.Y - h / 2, w, h));
    }

    /// <summary>
    /// What is placed on a page and not saved yet, bottom to top as it will be in the file: stamps, texts, pasted areas
    /// (they cover what lies under them), boxes.
    /// </summary>
    private void DrawOverlays(DrawingContext context, PageEntry page, Rect target)
    {
        DrawPlacedStamps(context, page, target, pasted: false);
        DrawPlacedTexts(context, page, target);
        DrawPlacedStamps(context, page, target, pasted: true);
        DrawRedactions(context, page, target);
    }

    /// <summary>The stamps, or with <paramref name="pasted"/> the pasted areas (<see cref="StampOverlay.CoversContent"/>).</summary>
    private void DrawPlacedStamps(DrawingContext context, PageEntry page, Rect target, bool pasted)
    {
        if (page.Stamps.Count == 0)
            return;
        var pxPerPt = target.Width / page.DisplaySize.Width;
        using (context.PushClip(target))
            foreach (var stamp in page.Stamps)
            {
                if (stamp.CoversContent != pasted || StampBitmap(stamp) is not { } bitmap)
                    continue;
                var r = _stampDrag is { } drag && drag.Id == stamp.Id ? drag.Current : PageGeometry.Rotate(stamp.Rect, page.BaseSize, page.ExtraRotation);
                var covered = new Rect(target.X + r.X * pxPerPt, target.Y + r.Y * pxPerPt, r.Width * pxPerPt, r.Height * pxPerPt);
                DrawStampImage(context, bitmap, covered, (stamp.Rotation + page.ExtraRotation) % 360, 1);
                if (stamp.Id == SelectedStampId && !_capturing)
                    DrawSelection(context, covered);
            }
    }

    private static void DrawSelection(DrawingContext context, Rect covered)
    {
        context.DrawRectangle(new Pen(Brushes.DodgerBlue, 1, DashStyle.Dash), covered);
        foreach (var corner in Corners(covered))
            context.DrawRectangle(Brushes.White, new Pen(Brushes.DodgerBlue, 1), new Rect(corner.X - HandleSize / 2, corner.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    /// <summary>Where the pending stamp would go if placed at <paramref name="position"/>: page index and rectangle in the page as shown (points).</summary>
    private (int Page, RectPt Rect)? StampTargetAt(Point position)
    {
        var bitmap = PendingStampImage;
        var pages = _document?.Pages;
        var index = PageIndexAt(position);
        if (bitmap == null || pages == null || index < 0 || index >= pages.Count)
            return null;
        var b = _layout.PageBounds[index];
        var size = pages[index].DisplaySize;
        var pxPerPt = b.Width / size.Width;
        var (w, h) = PendingStampSize is var (pw, ph)
            ? PageGeometry.FitInside(pw, ph, size)
            : PageGeometry.InitialStampSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height, size, PendingStampDpi);
        var cx = (_scrollX + position.X - b.X) / pxPerPt;
        var cy = (_scrollY + position.Y - b.Y) / pxPerPt;
        return (index, PageGeometry.CenteredInside(cx, cy, w, h, size));
    }

    #region Text

    public bool IsTextToolActive
    {
        get => GetValue(IsTextToolActiveProperty);
        set => SetValue(IsTextToolActiveProperty, value);
    }

    /// <summary>Style of new text (set by the window from the settings).</summary>
    public string PenColor { get; set; } = "#000000";

    public string PenFont { get; set; } = "Helvetica";

    public double PenSize { get; set; } = 12;

    /// <summary>A new text was typed: page index, the area it covers in the page as shown (points), the text.</summary>
    public event Action<int, RectPt, string>? TextPlaceRequested;

    /// <summary>A placed text was edited: page index, text id, new area in the page as shown, new text (empty = remove).</summary>
    public event Action<int, int, RectPt, string>? TextChangeRequested;

    /// <summary>The editor for a new text was opened by a click of the text tool.</summary>
    public event Action? TextEditorOpened;

    private TextBox? _textEditor;
    private int _editPage = -1;
    private int _editTextId = -1; // -1 = new text
    private Point _editOrigin;    // top-left of the text in the page as shown (points)
    private string _editFont = "Helvetica";
    private double _editSize = 12;

    private static Avalonia.Media.IBrush BrushFor(string color) =>
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color));

    /// <summary>Gives the ready text of a template for a click on the page with the given index (null = no template, open the editor).</summary>
    public Func<int, string?>? TemplateProvider { get; set; }

    private void BeginNewText(Point position)
    {
        var pages = _document?.Pages;
        var index = PageIndexAt(position);
        if (pages == null || index < 0 || index >= pages.Count)
            return; // a click beside the pages does nothing; the tool stays
        if (TemplateTargetAt(position) is var (_, template, area))
        {
            // A template is placed at once where its preview was; double click edits it
            TextEditorOpened?.Invoke(); // the tool is used up
            TextPlaceRequested?.Invoke(index, area, template);
            return;
        }
        var b = _layout.PageBounds[index];
        var pxPerPt = b.Width / pages[index].DisplaySize.Width;
        var origin = new Point((_scrollX + position.X - b.X) / pxPerPt, (_scrollY + position.Y - b.Y) / pxPerPt);
        OpenTextEditor(index, -1, origin, "", PenFont, PenSize, PenColor);
        TextEditorOpened?.Invoke(); // the tool is used up: Esc now belongs to the editor
    }

    /// <summary>
    /// Where the pending template would go for a click at <paramref name="position"/>: page index, its ready text and the
    /// area it covers in the page as shown (points), top-left corner at the position; null = no template or no page there.
    /// </summary>
    private (int Page, string Text, RectPt Rect)? TemplateTargetAt(Point position)
    {
        var pages = _document?.Pages;
        var index = PageIndexAt(position);
        if (pages == null || index < 0 || index >= pages.Count || TemplateProvider?.Invoke(index) is not { } template)
            return null;
        var b = _layout.PageBounds[index];
        var pxPerPt = b.Width / pages[index].DisplaySize.Width;
        var x = (_scrollX + position.X - b.X) / pxPerPt;
        var y = (_scrollY + position.Y - b.Y) / pxPerPt;
        template = template.Replace("\r\n", "\n").TrimEnd();
        var (w, h) = template.Length == 0 ? (0.0, 0.0) : TextMetrics.Measure(template, PenFont, PenSize);
        return (index, template, PageGeometry.CenteredInside(x + w / 2, y + h / 2, w, h, pages[index].DisplaySize));
    }

    /// <summary>Over a page the text of the pending template takes the place of the pointer, which would only hide its first letters.</summary>
    private void UpdateTemplatePreview(Point position)
    {
        var shown = TemplateTargetAt(position) is { Text.Length: > 0 };
        if (shown || _stampHover != null)
            _surface.InvalidateVisual();
        _stampHover = shown ? position : null;
        _surface.Cursor = new Cursor(shown ? StandardCursorType.None : StandardCursorType.Ibeam);
    }

    /// <summary>The pending template as it would be placed: real font, size and colour, only the frame marks it as not placed yet.</summary>
    private void DrawTemplatePreview(DrawingContext context)
    {
        if (!IsTextToolActive || _stampHover is not { } hover || TemplateTargetAt(hover) is not var (index, text, rect))
            return;
        var b = _layout.PageBounds[index];
        var pxPerPt = b.Width / _document!.Pages[index].DisplaySize.Width;
        var covered = StampScreenRect(index, rect);
        using (context.PushClip(new Rect(b.X - _scrollX, b.Y - _scrollY, b.Width, b.Height)))
            context.DrawText(TextMetrics.Format(text, PenFont, PenSize * pxPerPt, BrushFor(PenColor)), covered.TopLeft);
        context.DrawRectangle(new Pen(Brushes.DodgerBlue, 1, DashStyle.Dash), covered);
    }

    /// <summary>Double click on a stamp with a date field asks to change the date; what lies above it (a pasted area) takes the click.</summary>
    private bool TryEditStampAt(Point position)
    {
        var pages = _document?.Pages;
        var index = PageIndexAt(position);
        if (pages == null || index < 0 || index >= pages.Count)
            return false;
        var page = pages[index];
        foreach (var stamp in page.Stamps.Where(s => s.CoversContent).Reverse().Concat(page.Stamps.Where(s => !s.CoversContent).Reverse()))
        {
            if (!StampScreenRect(index, PageGeometry.Rotate(stamp.Rect, page.BaseSize, page.ExtraRotation)).Contains(position))
                continue;
            if (stamp.Template is not { Definition.UsesDate: true })
                return false;
            SelectedStampId = stamp.Id;
            StampDateEditRequested?.Invoke(stamp.Id);
            return true;
        }
        return false;
    }

    /// <summary>Double click on an upright placed text opens the editor on it.</summary>
    private bool TryEditTextAt(Point position)
    {
        var pages = _document?.Pages;
        var index = PageIndexAt(position);
        if (pages == null || index < 0 || index >= pages.Count)
            return false;
        var page = pages[index];
        for (var i = page.Texts.Count - 1; i >= 0; i--)
        {
            var text = page.Texts[i];
            var shown = PageGeometry.Rotate(text.Rect, page.BaseSize, page.ExtraRotation);
            if (!StampScreenRect(index, shown).Contains(position))
                continue;
            if ((text.Rotation + page.ExtraRotation) % 360 != 0)
                return false; // turned text cannot be edited in place
            OpenTextEditor(index, text.Id, new Point(shown.X, shown.Y), text.Text, text.Font, text.FontSize, text.Color);
            return true;
        }
        return false;
    }

    private void OpenTextEditor(int pageIndex, int textId, Point origin, string initial, string font, double sizePt, string color)
    {
        CommitTextEditor();
        var b = _layout.PageBounds[pageIndex];
        var pxPerPt = b.Width / _document!.Pages[pageIndex].DisplaySize.Width;
        var editor = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = TextMetrics.Family(font),
            FontSize = Math.Max(4, sizePt * pxPerPt),
            Foreground = BrushFor(color),
            CaretBrush = BrushFor(color),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White, 0.7),
            BorderBrush = Avalonia.Media.Brushes.DodgerBlue,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            MinWidth = 40,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(b.X - _scrollX + origin.X * pxPerPt - 1, b.Y - _scrollY + origin.Y * pxPerPt - 1, 0, 0),
        };
        SetColumn(editor, 0);
        SetRow(editor, 0);
        _textEditor = editor;
        _editPage = pageIndex;
        _editTextId = textId;
        _editOrigin = origin;
        _editFont = font;
        _editSize = sizePt;
        // Tunnelling and also when already handled: the window's own Esc shortcuts must not swallow the end of editing
        editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                CommitTextEditor();
                Focus();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        editor.LostFocus += (_, _) => CommitTextEditor();
        Children.Add(editor);
        _surface.InvalidateVisual(); // the text being edited is not drawn underneath
        editor.Focus();
    }

    private void CancelTextEditor()
    {
        if (_textEditor is not { } editor)
            return;
        _textEditor = null;
        Children.Remove(editor);
        _surface.InvalidateVisual();
    }

    /// <summary>Closes the editor and hands the text on; empty text on a new one just closes it.</summary>
    private void CommitTextEditor()
    {
        if (_textEditor is not { } editor)
            return;
        var text = (editor.Text ?? "").Replace("\r\n", "\n").TrimEnd();
        var (page, id, origin, font, size) = (_editPage, _editTextId, _editOrigin, _editFont, _editSize);
        CancelTextEditor(); // first: raising the events below may move focus and re-enter
        if (_document == null || page < 0 || page >= _document.PageCount)
            return;
        var shown = _document.Pages[page].DisplaySize;
        var (w, h) = text.Length == 0 ? (0.0, 0.0) : TextMetrics.Measure(text, font, size);
        var rect = PageGeometry.CenteredInside(origin.X + w / 2, origin.Y + h / 2, w, h, shown);
        if (id >= 0)
            TextChangeRequested?.Invoke(page, id, rect, text);
        else
            TextPlaceRequested?.Invoke(page, rect, text);
    }

    private void DrawPlacedTexts(DrawingContext context, PageEntry page, Rect target)
    {
        if (page.Texts.Count == 0)
            return;
        var pxPerPt = target.Width / page.DisplaySize.Width;
        using (context.PushClip(target))
            foreach (var text in page.Texts)
            {
                if (_textEditor != null && text.Id == _editTextId)
                    continue; // the editor shows it
                var dragged = _stampDrag is { } drag && drag.Id == text.Id ? drag : null;
                var r = dragged?.Current ?? PageGeometry.Rotate(text.Rect, page.BaseSize, page.ExtraRotation);
                var fontSize = dragged != null ? ScaledFontSize(dragged) : text.FontSize;
                var covered = new Rect(target.X + r.X * pxPerPt, target.Y + r.Y * pxPerPt, r.Width * pxPerPt, r.Height * pxPerPt);
                var degrees = (text.Rotation + page.ExtraRotation) % 360;
                var centre = covered.Center;
                var sideways = degrees is 90 or 270;
                var (w, h) = sideways ? (covered.Height, covered.Width) : (covered.Width, covered.Height);
                var transform = Matrix.CreateTranslation(-centre.X, -centre.Y) * Matrix.CreateRotation(degrees * Math.PI / 180) * Matrix.CreateTranslation(centre.X, centre.Y);
                var formatted = TextMetrics.Format(text.Text, text.Font, fontSize * pxPerPt, BrushFor(text.Color));
                using (context.PushTransform(transform))
                    context.DrawText(formatted, new Point(centre.X - w / 2, centre.Y - h / 2));
                if (text.Id == SelectedStampId && !_capturing)
                    DrawSelection(context, covered);
            }
    }

    #endregion

    private const double HandleSize = 9;
    private const double MinStampPt = 12;

    /// <summary>
    /// A stamp, text or redaction box being moved/resized; <paramref name="FontSize"/> is 0 for a stamp or box. A box
    /// (<paramref name="IsRedaction"/>) is resized freely, the others keep their aspect ratio.
    /// </summary>
    private sealed record StampDrag(int PageIndex, int Id, double FontSize, RectPt Start, int Corner, Point PointerStart, RectPt Current, bool IsRedaction = false);

    private StampDrag? _stampDrag;

    /// <summary>A text's font size follows the width of its box while it is resized.</summary>
    private static double ScaledFontSize(StampDrag drag) =>
        Math.Round(Math.Clamp(drag.FontSize * drag.Current.Width / drag.Start.Width, 4, 200), 1);

    private static Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];

    private Rect StampScreenRect(int pageIndex, RectPt viewRect)
    {
        var b = _layout.PageBounds[pageIndex];
        var pxPerPt = b.Width / _document!.Pages[pageIndex].DisplaySize.Width;
        return new Rect(b.X - _scrollX + viewRect.X * pxPerPt, b.Y - _scrollY + viewRect.Y * pxPerPt, viewRect.Width * pxPerPt, viewRect.Height * pxPerPt);
    }

    /// <summary>Starts moving (inside a stamp) or resizing (on a corner of the selected stamp); false = no stamp hit.</summary>
    private bool TryBeginStampDrag(Point position)
    {
        var pages = _document?.Pages;
        var index = PageIndexAt(position);
        if (pages == null || index < 0 || index >= pages.Count || pages[index].Stamps.Count + pages[index].Texts.Count + pages[index].Redactions.Count == 0)
            return false;
        var page = pages[index];
        // Stamps, texts, pasted areas and boxes together, bottom to top as they are drawn; ids are unique across all of them
        var items = page.Stamps.Where(s => !s.CoversContent).Select(s => (s.Id, s.Rect, FontSize: 0.0, IsRedaction: false))
            .Concat(page.Texts.Select(t => (t.Id, t.Rect, FontSize: t.FontSize, IsRedaction: false)))
            .Concat(page.Stamps.Where(s => s.CoversContent).Select(s => (s.Id, s.Rect, FontSize: 0.0, IsRedaction: false)))
            .Concat(page.Redactions.Select(r => (r.Id, r.Rect, FontSize: 0.0, IsRedaction: true))).ToList();
        (int Id, RectPt Rect, double FontSize, bool IsRedaction)? hit = null;
        var corner = -1;
        // The selected item's handles reach slightly outside it, so they are tested first
        if (items.FirstOrDefault(s => s.Id == SelectedStampId) is { Id: > 0 } selected)
        {
            var corners = Corners(StampScreenRect(index, PageGeometry.Rotate(selected.Rect, page.BaseSize, page.ExtraRotation)));
            for (var i = 0; i < 4; i++)
                if (Math.Abs(corners[i].X - position.X) <= HandleSize && Math.Abs(corners[i].Y - position.Y) <= HandleSize)
                {
                    hit = selected;
                    corner = i;
                    break;
                }
        }
        if (hit == null)
            for (var i = items.Count - 1; i >= 0; i--) // topmost first
                if (StampScreenRect(index, PageGeometry.Rotate(items[i].Rect, page.BaseSize, page.ExtraRotation)).Contains(position))
                {
                    hit = items[i];
                    break;
                }
        if (hit is not { } found)
            return false;
        SelectedStampId = found.Id;
        var start = PageGeometry.Rotate(found.Rect, page.BaseSize, page.ExtraRotation);
        _stampDrag = new StampDrag(index, found.Id, found.FontSize, start, corner, position, start, found.IsRedaction);
        _surface.Cursor = new Cursor(corner >= 0 ? StandardCursorType.BottomRightCorner : StandardCursorType.SizeAll);
        return true;
    }

    private void UpdateStampDrag(Point position)
    {
        if (_stampDrag is not { } drag || _document == null)
            return;
        var size = _document.Pages[drag.PageIndex].DisplaySize;
        var b = _layout.PageBounds[drag.PageIndex];
        var pxPerPt = b.Width / size.Width;
        var dx = (position.X - drag.PointerStart.X) / pxPerPt;
        var dy = (position.Y - drag.PointerStart.Y) / pxPerPt;
        var s = drag.Start;
        RectPt result;
        if (drag.Corner < 0)
        {
            result = new RectPt(Math.Clamp(s.X + dx, 0, Math.Max(0, size.Width - s.Width)), Math.Clamp(s.Y + dy, 0, Math.Max(0, size.Height - s.Height)), s.Width, s.Height);
        }
        else if (drag.IsRedaction)
        {
            // The opposite corner stays, the dragged one follows the pointer in both directions
            var left = drag.Corner is 0 or 3;
            var up = drag.Corner is 0 or 1;
            var anchorX = left ? s.Right : s.X;
            var anchorY = up ? s.Bottom : s.Y;
            var pointerX = Math.Clamp((_scrollX + position.X - b.X) / pxPerPt, 0, size.Width);
            var pointerY = Math.Clamp((_scrollY + position.Y - b.Y) / pxPerPt, 0, size.Height);
            var width = Math.Max(MinRedactionPt, left ? anchorX - pointerX : pointerX - anchorX);
            var height = Math.Max(MinRedactionPt, up ? anchorY - pointerY : pointerY - anchorY);
            result = new RectPt(left ? anchorX - width : anchorX, up ? anchorY - height : anchorY, width, height);
        }
        else
        {
            // The corner opposite to the dragged one stays; the width follows the pointer, the height keeps the aspect ratio
            var left = drag.Corner is 0 or 3;
            var up = drag.Corner is 0 or 1;
            var anchorX = left ? s.Right : s.X;
            var anchorY = up ? s.Bottom : s.Y;
            var aspect = s.Height / s.Width;
            var pointerX = (_scrollX + position.X - b.X) / pxPerPt;
            var width = left ? anchorX - pointerX : pointerX - anchorX;
            var room = Math.Min(left ? anchorX : size.Width - anchorX, (up ? anchorY : size.Height - anchorY) / aspect);
            width = Math.Clamp(width, Math.Min(MinStampPt, room), Math.Max(MinStampPt, room));
            var height = width * aspect;
            result = new RectPt(left ? anchorX - width : anchorX, up ? anchorY - height : anchorY, width, height);
        }
        _stampDrag = drag with { Current = result };
        _surface.InvalidateVisual();
    }

    private void EndStampDrag(bool commit)
    {
        if (_stampDrag is not { } drag)
            return;
        _stampDrag = null;
        _surface.Cursor = null;
        _surface.InvalidateVisual();
        if (!commit || _document == null || drag.Current == drag.Start)
            return;
        var page = _document.Pages[drag.PageIndex];
        var rect = PageGeometry.Unrotate(drag.Current, page.BaseSize, page.ExtraRotation);
        if (drag.IsRedaction)
            RedactionChangeRequested?.Invoke(drag.Id, rect);
        else if (drag.FontSize > 0)
            TextBoxChangeRequested?.Invoke(drag.Id, rect, ScaledFontSize(drag));
        else
            StampChangeRequested?.Invoke(drag.Id, rect);
    }

    private void DrawStampPreview(DrawingContext context, IReadOnlyList<PageEntry> pages)
    {
        if (PendingStampImage is not { } bitmap || _stampHover is not { } hover || StampTargetAt(hover) is not var (index, rect))
            return;
        var b = _layout.PageBounds[index];
        var pxPerPt = b.Width / pages[index].DisplaySize.Width;
        var covered = new Rect(b.X - _scrollX + rect.X * pxPerPt, b.Y - _scrollY + rect.Y * pxPerPt, rect.Width * pxPerPt, rect.Height * pxPerPt);
        DrawStampImage(context, bitmap, covered, 0, StampPreviewOpacity);
        context.DrawRectangle(new Pen(Brushes.DodgerBlue, 1, DashStyle.Dash), covered);
    }

    private void RequestStampPlacement(Point position)
    {
        if (StampTargetAt(position) is var (index, rect))
            StampPlaceRequested?.Invoke(index, rect);
    }

    #endregion

    private static void DrawCentered(DrawingContext context, string text, Rect target, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 14, brush);
        var origin = new Point(target.X + (target.Width - formatted.Width) / 2, target.Y + (target.Height - formatted.Height) / 2);
        using (context.PushClip(target))
            context.DrawText(formatted, origin);
    }

    private sealed class PageSurface : Control
    {
        private readonly HorizontalPageView _owner;

        public PageSurface(HorizontalPageView owner)
        {
            _owner = owner;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext context) => _owner.Paint(context, Bounds.Size);
    }

    #endregion
}
