using System;
using System.Linq;
using EasyPDF.Core.Caching;
using EasyPDF.Core.Layout;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

public class HorizontalLayoutTests
{
    private static HorizontalLayout Make(ZoomMode mode, double custom = 100, int pages = 10)
    {
        var layout = new HorizontalLayout();
        layout.Update([.. Enumerable.Repeat(new PageSizePt(300, 400), pages)], 800, 600, mode, custom);
        return layout;
    }

    [Fact]
    public void FitHeightFillsViewportHeight()
    {
        var l = Make(ZoomMode.FitHeight);
        var b = l.PageBounds[0];
        Assert.Equal(600 - 2 * HorizontalLayout.OuterMargin, b.Height);
        Assert.Equal(Math.Round(300 * b.Height / 400), b.Width);
    }

    [Fact]
    public void FitWidthFillsViewportWidth() =>
        Assert.Equal(800 - 2 * HorizontalLayout.OuterMargin, Make(ZoomMode.FitWidth).PageBounds[0].Width);

    [Fact]
    public void FitBestUsesSmallerScale()
    {
        var b = Make(ZoomMode.FitBest).PageBounds[0];
        Assert.True(b.Width <= 800 - 23 && b.Height <= 600 - 23);
    }

    [Fact]
    public void CustomZoom100IsRealSizeAt96Dpi()
    {
        var l = Make(ZoomMode.Custom, 100);
        Assert.Equal(400, l.PageBounds[0].Width); // 300 pt * 96/72
        Assert.Equal(100, l.ZoomPercentOf(0), 0.5);
    }

    [Fact]
    public void ZoomIsClamped()
    {
        Assert.Equal(HorizontalLayout.MaxZoomPercent, Make(ZoomMode.Custom, 5000).CustomZoomPercent);
        Assert.Equal(HorizontalLayout.MinZoomPercent, Make(ZoomMode.Custom, 1).CustomZoomPercent);
    }

    [Fact]
    public void PagesAreSideBySideWithGap()
    {
        var l = Make(ZoomMode.FitHeight);
        Assert.Equal(l.PageBounds[0].Right + HorizontalLayout.PageGap, l.PageBounds[1].X);
        Assert.Equal(l.PageBounds[9].Right + HorizontalLayout.OuterMargin, l.ContentWidth);
    }

    [Fact]
    public void FindPageAtAndVisibleRange()
    {
        var l = Make(ZoomMode.FitHeight);
        Assert.Equal(0, l.FindPageAt(0));
        Assert.Equal(3, l.FindPageAt(l.PageBounds[3].X + 1));
        var (first, last) = l.VisibleRange(l.PageBounds[2].X, 800);
        Assert.Equal(2, first);
        Assert.True(last >= 3);
        // scrolled into the gap after page 2 -> page 3 is the first visible one
        Assert.Equal(3, l.VisibleRange(l.PageBounds[2].Right + 1, 800).First);
    }

    [Fact]
    public void AnchorSurvivesRelayout()
    {
        var l = Make(ZoomMode.FitHeight);
        var x = l.PageBounds[4].X + l.PageBounds[4].Width * 0.25;
        var (page, fraction) = l.GetAnchor(x);
        l.Update([.. Enumerable.Repeat(new PageSizePt(300, 400), 10)], 800, 600, ZoomMode.Custom, 250);
        Assert.Equal(4, page);
        Assert.Equal(l.PageBounds[4].X + l.PageBounds[4].Width * 0.25, l.GetXForAnchor(page, fraction), 3);
    }

    [Fact]
    public void PagesAreCenteredVerticallyWhenSmaller()
    {
        var l = new HorizontalLayout();
        l.Update([new PageSizePt(100, 100)], 800, 600, ZoomMode.Custom, 50);
        var b = l.PageBounds[0];
        Assert.Equal(Math.Floor((600 - b.Height) / 2), b.Y);
    }

    [Fact]
    public void EmptyLayoutIsSafe()
    {
        var l = new HorizontalLayout();
        l.Update([], 800, 600, ZoomMode.FitHeight, 100);
        Assert.Equal(-1, l.FindPageAt(0));
        Assert.Equal((0, -1), l.VisibleRange(0, 800));
        Assert.Equal(0, l.ContentWidth);
    }
}

public class PageImageCacheTests
{
    private sealed class Img : IDisposable
    {
        public long Bytes;
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static PageImageCache<int, Img> Make(long budget) => new(budget, i => i.Bytes);

    [Fact]
    public void EvictsLeastRecentlyUsed()
    {
        var c = Make(250);
        var a = new Img { Bytes = 100 };
        var b = new Img { Bytes = 100 };
        var d = new Img { Bytes = 100 };
        c.Set(1, a);
        c.Set(2, b);
        Assert.True(c.TryGet(1, out _)); // 1 is now more recent than 2
        c.Set(3, d);
        Assert.True(c.Contains(1));
        Assert.False(c.Contains(2));
        Assert.True(b.Disposed);
        Assert.Equal(200, c.UsedBytes);
    }

    [Fact]
    public void ProtectedEntriesAreNeverEvicted()
    {
        var c = Make(150);
        c.Set(1, new Img { Bytes = 100 });
        c.Set(2, new Img { Bytes = 100 }, k => k <= 2);
        Assert.Equal(2, c.Count);
        Assert.Equal(200, c.UsedBytes);
        c.Trim(k => k == 2);
        Assert.False(c.Contains(1));
    }

    [Fact]
    public void ReplacingDisposesOldValue()
    {
        var c = Make(1000);
        var old = new Img { Bytes = 10 };
        c.Set(1, old);
        c.Set(1, new Img { Bytes = 30 });
        Assert.True(old.Disposed);
        Assert.Equal(30, c.UsedBytes);
        c.Clear();
        Assert.Equal(0, c.UsedBytes);
        Assert.Equal(0, c.Count);
    }
}
