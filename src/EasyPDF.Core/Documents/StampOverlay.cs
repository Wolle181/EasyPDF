using System;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Stamps;
using EasyPDF.Core.Templates;

namespace EasyPDF.Core.Documents;

/// <summary>A rectangle in points; origin top-left, y grows downwards.</summary>
public readonly record struct RectPt(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>
/// A picture placed on a page (not saved yet). The rectangle lives in the page's own space: the source page size after its
/// own /Rotate but before the extra rotation of the editor, origin top-left, in points. This way the stamp stays where it
/// is when the page is rotated later; use <see cref="PageGeometry"/> to convert to and from the rotated view.
/// </summary>
/// <param name="Rotation">Clockwise turn of the picture inside page space (0, 90, 180, 270); <see cref="Rect"/> is the area it covers there.</param>
/// <param name="Bgra">The decoded picture for saving: <c>PixelWidth * PixelHeight * 4</c> bytes, B, G, R, A with straight (not premultiplied) alpha; null = cannot be saved.</param>
/// <param name="Image">The picture file as read from the stamp folder (PNG); a copy, so later changes in the folder don't matter.</param>
/// <param name="CoversContent">
/// A pasted area (copied from a page): opaque, drawn above stamps and texts, and what lies under it is removed from the
/// file when saving, like under a white-out box (<see cref="PageEntry.RemovalAreas"/>).
/// </param>
/// <param name="Template">A stamp with fields: what its picture was made from, so it can be made again with another date; null = a plain picture.</param>
public sealed record StampOverlay(int Id, byte[] Image, int PixelWidth, int PixelHeight, RectPt Rect, int Rotation = 0, byte[]? Bgra = null,
    bool CoversContent = false, StampTemplate? Template = null);

/// <summary>A placed stamp's fields: the picture file without their text, the fields, and what their macros stood for when it was placed.</summary>
public sealed record StampTemplate(byte[] Picture, StampDefinition Definition, TemplateContext Context);

/// <summary>Rotation maths between a page and its rotated view, and where a new stamp goes.</summary>
public static class PageGeometry
{
    /// <summary>Rect on a page of size <paramref name="page"/> as seen after turning the page clockwise by <paramref name="degrees"/>.</summary>
    public static RectPt Rotate(RectPt r, PageSizePt page, int degrees) => Normalize(degrees) switch
    {
        90 => new RectPt(page.Height - r.Y - r.Height, r.X, r.Height, r.Width),
        180 => new RectPt(page.Width - r.X - r.Width, page.Height - r.Y - r.Height, r.Width, r.Height),
        270 => new RectPt(r.Y, page.Width - r.X - r.Width, r.Height, r.Width),
        _ => r,
    };

    /// <summary>The inverse of <see cref="Rotate"/>: a rect in the rotated view back to page space.</summary>
    public static RectPt Unrotate(RectPt r, PageSizePt page, int degrees) =>
        Rotate(r, page.Rotated(degrees), 360 - Normalize(degrees));

    /// <summary>
    /// Size in points of a newly placed stamp: the picture at the resolution stored in it (<paramref name="dpi"/>; 96 dpi,
    /// i.e. 1 px = 0.75 pt, if it has none), reduced to fit into half the page (keeping the aspect ratio), never enlarged.
    /// </summary>
    public static (double Width, double Height) InitialStampSize(int pixelWidth, int pixelHeight, PageSizePt viewedPage, double? dpi = null)
    {
        var ptPerPixel = 72 / (dpi ?? 96);
        double w = Math.Max(1, pixelWidth) * ptPerPixel, h = Math.Max(1, pixelHeight) * ptPerPixel;
        var scale = Math.Min(1, Math.Min(viewedPage.Width * 0.5 / w, viewedPage.Height * 0.5 / h));
        return (w * scale, h * scale);
    }

    /// <summary>
    /// Size in points of a pasted area: its size when it was copied, reduced to fit into the page (keeping the aspect
    /// ratio), never enlarged.
    /// </summary>
    public static (double Width, double Height) FitInside(double width, double height, PageSizePt viewedPage)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var scale = Math.Min(1, Math.Min(viewedPage.Width / width, viewedPage.Height / height));
        return (width * scale, height * scale);
    }

    /// <summary>A rect of the given size centred on a point, moved so that it lies completely inside the page.</summary>
    public static RectPt CenteredInside(double centerX, double centerY, double width, double height, PageSizePt page)
    {
        var x = Math.Clamp(centerX - width / 2, 0, Math.Max(0, page.Width - width));
        var y = Math.Clamp(centerY - height / 2, 0, Math.Max(0, page.Height - height));
        return new RectPt(x, y, width, height);
    }

    private static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;
}
