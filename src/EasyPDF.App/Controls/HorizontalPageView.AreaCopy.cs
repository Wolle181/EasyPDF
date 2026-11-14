using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EasyPDF.App.Services;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.Controls;

/// <summary>
/// Copy area: with the tool on, a drag draws a frame around a part of a page; on release that part is copied as a
/// picture, as it is shown (the page with its placed stamps, texts and boxes, upright as in the view). Pasting places it
/// like a stamp (<see cref="PendingStampSize"/>: in the size it was copied).
/// </summary>
public sealed partial class HorizontalPageView
{
    private const double CopyDpi = 300;
    private const double MaxCopyPixels = 40e6; // about A3 at 300 dpi; larger areas get a lower resolution

    /// <summary>True while the copy-area tool is on: a drag copies the area framed.</summary>
    public static readonly StyledProperty<bool> IsCopyAreaToolActiveProperty =
        AvaloniaProperty.Register<HorizontalPageView, bool>(nameof(IsCopyAreaToolActive));

    public bool IsCopyAreaToolActive
    {
        get => GetValue(IsCopyAreaToolActiveProperty);
        set => SetValue(IsCopyAreaToolActiveProperty, value);
    }

    /// <summary>
    /// Size in points (as shown) of the pending picture when it is placed; null = from its pixels, like a stamp from the
    /// stamp folder. Pasted areas keep the size they were copied in (reduced only to fit into the page).
    /// </summary>
    public (double Width, double Height)? PendingStampSize { get; set; }

    /// <summary>Resolution stored in the pending stamp's picture (it is placed in the size this gives); null = 96 dpi.</summary>
    public double? PendingStampDpi { get; set; }

    /// <summary>An area was copied with the tool.</summary>
    public event Action<CopiedArea>? AreaCopied;

    /// <summary>Copying an area failed (e.g. the page could not be rendered).</summary>
    public event Action<Exception>? AreaCopyFailed;

    private bool _capturing; // drawing into a copied area: no selection frames or screen-only outlines

    private void BeginAreaCopy(Point position)
    {
        var index = PageIndexAt(position);
        if (index < 0 || _document == null)
            return;
        MarkedPageId = _document.Pages[index].Id;
        var start = ToPagePoint(index, ToContent(position));
        _areaDrag = new AreaDrag(index, start, new RectPt(start.X, start.Y, 0, 0));
    }

    private async Task CopyAreaAsync(int pageIndex, RectPt viewRect)
    {
        try
        {
            if (await CaptureAreaAsync(pageIndex, viewRect) is { } area)
                AreaCopied?.Invoke(area);
        }
        catch (Exception ex)
        {
            AreaCopyFailed?.Invoke(ex);
        }
    }

    /// <summary>
    /// The area <paramref name="viewRect"/> of a page (points, as shown) as a picture at 300 dpi with everything placed on
    /// it; null if the page is no longer there.
    /// </summary>
    public async Task<CopiedArea?> CaptureAreaAsync(int pageIndex, RectPt viewRect)
    {
        if (_document == null || pageIndex < 0 || pageIndex >= _document.PageCount || viewRect.Width <= 0 || viewRect.Height <= 0)
            return null;
        var entry = _document.Pages[pageIndex]; // this state is copied, also if the document changes meanwhile
        var shown = entry.DisplaySize;
        var scale = CopyDpi / 72;
        var pixels = viewRect.Width * scale * viewRect.Height * scale;
        if (pixels > MaxCopyPixels)
            scale *= Math.Sqrt(MaxCopyPixels / pixels);
        int x0 = (int)Math.Floor(viewRect.X * scale), y0 = (int)Math.Floor(viewRect.Y * scale);
        int x1 = Math.Max(x0 + 1, (int)Math.Ceiling(viewRect.Right * scale)), y1 = Math.Max(y0 + 1, (int)Math.Ceiling(viewRect.Bottom * scale));
        int w = x1 - x0, h = y1 - y0;
        var (pageW, pageH) = (shown.Width * scale, shown.Height * scale);
        var rendered = await entry.Source.RenderAreaAsync(entry.SourceIndex, (int)Math.Round(pageW), (int)Math.Round(pageH), entry.ExtraRotation, x0, y0, w, h);

        using var page = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = page.Lock())
            for (var y = 0; y < h; y++)
                Marshal.Copy(rendered.Pixels, y * rendered.Stride, fb.Address + y * fb.RowBytes, w * 4);
        using var picture = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var context = picture.CreateDrawingContext())
        {
            context.DrawWholeBitmap(page, new Rect(0, 0, w, h));
            // The page as a whole, moved so that the copied part lies at the origin: the overlays are drawn as on screen
            var target = new Rect(-x0, -y0, pageW, pageH);
            _capturing = true;
            try
            {
                DrawOverlays(context, entry, target);
            }
            finally
            {
                _capturing = false;
            }
        }
        using var png = new MemoryStream();
        picture.Save(png, new PngBitmapEncoderOptions());
        return new CopiedArea(png.ToArray(), w, h, StampPixels.ToStraightBgra(picture), w / scale, h / scale);
    }
}
