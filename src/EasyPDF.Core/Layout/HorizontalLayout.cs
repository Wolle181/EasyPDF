using System;
using System.Collections.Generic;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Core.Layout;

public enum ZoomMode
{
    FitHeight,
    FitWidth,
    FitBest,

    /// <summary>Fixed percentage, see <see cref="HorizontalLayout.CustomZoomPercent"/>.</summary>
    Custom,
}

public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// Pure layout math for the reading view: pages side by side, each sized according to the zoom mode.
/// Units are device-independent pixels (1/96 inch). Content coordinates do not include the scroll offset.
/// </summary>
public sealed class HorizontalLayout
{
    public const double PageGap = 16;
    public const double OuterMargin = 12;
    public const double MinZoomPercent = 10;
    public const double MaxZoomPercent = 800;
    private const double PixelsPerPoint = 96.0 / 72.0;

    private LayoutRect[] _bounds = [];
    private double[] _scales = [];

    public ZoomMode Mode { get; private set; } = ZoomMode.FitHeight;

    /// <summary>Zoom in percent for <see cref="ZoomMode.Custom"/>; 100 = real size at 96 dpi.</summary>
    public double CustomZoomPercent { get; private set; } = 100;

    public int Count => _bounds.Length;

    public double ContentWidth { get; private set; }

    public double ContentHeight { get; private set; }

    public IReadOnlyList<LayoutRect> PageBounds => _bounds;

    /// <summary>Zoom of a page in percent (100 = real size).</summary>
    public double ZoomPercentOf(int page) => page >= 0 && page < _scales.Length ? _scales[page] / PixelsPerPoint * 100 : CustomZoomPercent;

    public static double ClampZoom(double percent) => Math.Clamp(percent, MinZoomPercent, MaxZoomPercent);

    /// <summary>
    /// Recomputes all page rectangles. <paramref name="viewportWidth"/>/<paramref name="viewportHeight"/> is the visible
    /// area; the caller should already subtract scrollbars that are always shown.
    /// </summary>
    public void Update(IReadOnlyList<PageSizePt> sizes, double viewportWidth, double viewportHeight, ZoomMode mode, double customZoomPercent)
    {
        Mode = mode;
        CustomZoomPercent = ClampZoom(customZoomPercent);

        var availW = Math.Max(1, viewportWidth - 2 * OuterMargin);
        var availH = Math.Max(1, viewportHeight - 2 * OuterMargin);

        var bounds = new LayoutRect[sizes.Count];
        var scales = new double[sizes.Count];
        double maxHeight = 0;
        var x = OuterMargin;
        for (var i = 0; i < sizes.Count; i++)
        {
            var w = Math.Max(1, sizes[i].Width);
            var h = Math.Max(1, sizes[i].Height);
            var scale = mode switch
            {
                ZoomMode.FitWidth => availW / w,
                ZoomMode.FitBest => Math.Min(availW / w, availH / h),
                ZoomMode.Custom => CustomZoomPercent / 100 * PixelsPerPoint,
                _ => availH / h,
            };
            // Whole pixels keep the rendered bitmap and the layout rectangle identical
            var pw = Math.Max(1, Math.Round(w * scale));
            var ph = Math.Max(1, Math.Round(h * scale));
            scales[i] = ph / h;
            bounds[i] = new LayoutRect(x, 0, pw, ph);
            x += pw + PageGap;
            maxHeight = Math.Max(maxHeight, ph);
        }

        ContentHeight = Math.Max(maxHeight + 2 * OuterMargin, viewportHeight);
        for (var i = 0; i < bounds.Length; i++)
            bounds[i] = bounds[i] with { Y = Math.Floor((ContentHeight - bounds[i].Height) / 2) };
        ContentWidth = bounds.Length == 0 ? 0 : x - PageGap + OuterMargin;
        _bounds = bounds;
        _scales = scales;
    }

    /// <summary>Last page whose left edge is at or before <paramref name="x"/> (binary search); -1 if empty.</summary>
    public int FindPageAt(double x)
    {
        if (_bounds.Length == 0)
            return -1;
        int lo = 0, hi = _bounds.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_bounds[mid].X <= x)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Pages intersecting the viewport; (0, -1) if there are none.</summary>
    public (int First, int Last) VisibleRange(double scrollX, double viewportWidth)
    {
        if (_bounds.Length == 0)
            return (0, -1);
        var first = FindPageAt(scrollX);
        if (first < _bounds.Length - 1 && _bounds[first].Right < scrollX)
            first++;
        var last = Math.Max(first, FindPageAt(scrollX + viewportWidth));
        return (first, last);
    }

    /// <summary>Which page is at content position <paramref name="x"/> and how far into it (0..1). Used to keep the position on relayout/zoom.</summary>
    public (int Page, double Fraction) GetAnchor(double x)
    {
        var page = FindPageAt(x);
        if (page < 0)
            return (0, 0);
        var b = _bounds[page];
        return (page, b.Width > 0 ? Math.Clamp((x - b.X) / b.Width, 0, 1) : 0);
    }

    /// <summary>Inverse of <see cref="GetAnchor"/> after a relayout.</summary>
    public double GetXForAnchor(int page, double fraction)
    {
        if (_bounds.Length == 0)
            return 0;
        var b = _bounds[Math.Clamp(page, 0, _bounds.Length - 1)];
        return b.X + fraction * b.Width;
    }

    /// <summary>Scroll offset that puts the left edge of a page at the left edge of the viewport (with outer margin).</summary>
    public double ScrollXForPage(int page) =>
        _bounds.Length == 0 ? 0 : Math.Max(0, _bounds[Math.Clamp(page, 0, _bounds.Length - 1)].X - OuterMargin);

    /// <summary>Maximum horizontal scroll offset.</summary>
    public double MaxScrollX(double viewportWidth) => Math.Max(0, ContentWidth - viewportWidth);
}
