using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;

namespace EasyPDF.App.Controls;

/// <summary>
/// Hits of the text search: all of them are highlighted on their pages, the current one more strongly; a hit that is
/// shown is scrolled into view (the search itself runs in the view model).
/// </summary>
public sealed partial class HorizontalPageView
{
    /// <summary>All hits of the search (empty = none). A hit applies to its page entry only, so a changed page drops it.</summary>
    public static readonly StyledProperty<IReadOnlyList<TextSearchHit>> SearchHitsProperty =
        AvaloniaProperty.Register<HorizontalPageView, IReadOnlyList<TextSearchHit>>(nameof(SearchHits), []);

    /// <summary>The current hit, highlighted more strongly (null = none).</summary>
    public static readonly StyledProperty<TextSearchHit?> CurrentSearchHitProperty =
        AvaloniaProperty.Register<HorizontalPageView, TextSearchHit?>(nameof(CurrentSearchHit));

    // Fixed colours like the text selection: they lie on the page, which is always white
    private static readonly IBrush SearchHitBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xD0, 0x00));
    private static readonly IBrush CurrentSearchHitBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0x7A, 0x00));
    private static readonly IPen CurrentSearchHitPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x00)), 1.5);

    /// <summary>Space kept around a hit that is scrolled into view (px).</summary>
    private const double SearchHitMargin = 40;

    private Dictionary<PageEntry, List<TextSearchHit>> _hitsByPage = new(ReferenceEqualityComparer.Instance);
    private TextSearchHit? _pendingSearchHit;

    public IReadOnlyList<TextSearchHit> SearchHits
    {
        get => GetValue(SearchHitsProperty);
        set => SetValue(SearchHitsProperty, value);
    }

    public TextSearchHit? CurrentSearchHit
    {
        get => GetValue(CurrentSearchHitProperty);
        set => SetValue(CurrentSearchHitProperty, value);
    }

    private void InitSearch()
    {
        SearchHitsProperty.Changed.AddClassHandler<HorizontalPageView>((v, _) =>
        {
            var byPage = new Dictionary<PageEntry, List<TextSearchHit>>(ReferenceEqualityComparer.Instance);
            foreach (var hit in v.SearchHits ?? [])
            {
                if (!byPage.TryGetValue(hit.Page, out var list))
                    byPage[hit.Page] = list = [];
                list.Add(hit);
            }
            v._hitsByPage = byPage;
            v._surface.InvalidateVisual();
        });
        CurrentSearchHitProperty.Changed.AddClassHandler<HorizontalPageView>((v, _) => v._surface.InvalidateVisual());
    }

    private int IndexOfEntry(PageEntry entry)
    {
        var pages = _document?.Pages;
        for (var i = 0; pages != null && i < pages.Count; i++)
            if (ReferenceEquals(pages[i], entry))
                return i;
        return -1;
    }

    /// <summary>Scrolls so that the hit is visible (centred if it was not); waits for the layout if the view is not shown yet.</summary>
    public void ScrollToSearchHit(TextSearchHit hit)
    {
        var index = IndexOfEntry(hit.Page);
        if (index < 0 || hit.Boxes.Count == 0)
            return;
        if (_layout.Count != _document!.PageCount || Viewport.Width < 1)
        {
            _pendingSearchHit = hit;
            return;
        }
        _pendingSearchHit = null;
        var b = _layout.PageBounds[index];
        var entry = hit.Page;
        var pxPerPt = b.Width / entry.DisplaySize.Width;
        var shown = hit.Boxes.Select(box => PageGeometry.Rotate(box, entry.BaseSize, entry.ExtraRotation)).ToList();
        double left = shown.Min(r => r.X), top = shown.Min(r => r.Y), right = shown.Max(r => r.Right), bottom = shown.Max(r => r.Bottom);
        var area = new Rect(b.X + left * pxPerPt, b.Y + top * pxPerPt, (right - left) * pxPerPt, (bottom - top) * pxPerPt);
        var view = new Rect(_scrollX, _scrollY, Viewport.Width, Viewport.Height).Deflate(Math.Min(SearchHitMargin, Viewport.Width / 4));
        var x = view.Left <= area.Left && area.Right <= view.Right ? _scrollX : area.Center.X - Viewport.Width / 2;
        var y = view.Top <= area.Top && area.Bottom <= view.Bottom ? _scrollY : area.Center.Y - Viewport.Height / 2;
        SetScroll(x, y);
    }

    /// <summary>Called after a layout: a hit that was asked for while the view was hidden is shown now.</summary>
    private void ShowPendingSearchHit()
    {
        if (_pendingSearchHit is { } hit)
            ScrollToSearchHit(hit);
    }

    private void DrawSearchHits(DrawingContext context, PageEntry page, Rect target)
    {
        if (!_hitsByPage.TryGetValue(page, out var hits))
            return;
        var pxPerPt = target.Width / page.DisplaySize.Width;
        Rect ToScreen(RectPt r) => new(target.X + r.X * pxPerPt, target.Y + r.Y * pxPerPt, r.Width * pxPerPt, r.Height * pxPerPt);
        var current = CurrentSearchHit;
        using (context.PushClip(target))
            foreach (var hit in hits)
            {
                var isCurrent = ReferenceEquals(hit, current);
                foreach (var box in hit.Boxes)
                {
                    var rect = ToScreen(PageGeometry.Rotate(box, page.BaseSize, page.ExtraRotation)).Inflate(1);
                    context.DrawRectangle(isCurrent ? CurrentSearchHitBrush : SearchHitBrush, isCurrent ? CurrentSearchHitPen : null, rect);
                }
            }
    }
}
