using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EasyPDF.App.Controls;

internal static class BitmapDrawing
{
    /// <summary>
    /// Draws the whole bitmap into <paramref name="target"/>. Avalonia reads DrawImage's source rectangle in pixels, not in
    /// DIPs: <c>new Rect(bitmap.Size)</c> (and the overload without source rectangle) draws only the top-left part of a
    /// bitmap rendered for a display scaling above 100 %.
    /// </summary>
    public static void DrawWholeBitmap(this DrawingContext context, Bitmap bitmap, Rect target) =>
        context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), target);
}
