using Avalonia;
using Avalonia.Media;

namespace EasyPDF.App.Controls;

/// <summary>Marks the page a context menu was opened for: a soft red glow plus a red frame.</summary>
internal static class ContextHighlight
{
    private static readonly Color _red = Color.FromRgb(0xE5, 0x48, 0x4D);

    public static void Draw(DrawingContext context, Rect pageRect)
    {
        context.DrawRectangle(null, new Pen(new SolidColorBrush(_red, 0.18), 10), pageRect.Inflate(5), 6, 6);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(_red, 0.40), 6), pageRect.Inflate(3), 4, 4);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(_red), 3), pageRect.Inflate(1.5), 2, 2);
    }
}
