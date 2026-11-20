using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EasyPDF.App.Controls;
using EasyPDF.Core.Stamps;
using EasyPDF.Core.Templates;

namespace EasyPDF.App.Services;

/// <summary>A stamp picture with the text of its fields drawn on it: the PNG and the pixels the saver needs.</summary>
public sealed record ComposedStamp(byte[] Png, int PixelWidth, int PixelHeight, byte[] Bgra);

/// <summary>
/// Writes the fields of a stamp into its picture. The result is an ordinary picture, so moving, turning, covering and saving
/// a placed stamp need to know nothing about fields.
/// </summary>
public static class StampComposer
{
    /// <summary>The program's own font: part of the program, so a stamp looks the same on every system.</summary>
    private static readonly Typeface Font = new(new FontFamily(new Uri($"avares://{typeof(StampComposer).Assembly.GetName().Name}"), "/Assets/Fonts#Barlow"),
        FontStyle.Normal, FontWeight.Bold);

    /// <summary>Any size: the outline of the text is scaled to its area afterwards.</summary>
    private const double OutlineSize = 200;

    public static ComposedStamp Compose(byte[] picture, StampDefinition definition, TemplateContext context)
    {
        using var stream = new MemoryStream(picture);
        using var bitmap = new Bitmap(stream);
        var size = bitmap.PixelSize;
        using var result = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var drawing = result.CreateDrawingContext())
        {
            drawing.DrawWholeBitmap(bitmap, new Rect(0, 0, size.Width, size.Height));
            foreach (var field in definition.Fields)
                DrawField(drawing, field, field.Expand(context), size);
        }
        using var png = new MemoryStream();
        result.Save(png, new PngBitmapEncoderOptions());
        return new ComposedStamp(png.ToArray(), size.Width, size.Height, StampPixels.ToStraightBgra(result));
    }

    /// <summary>
    /// The capital letters fill the height of the area (with lower case letters: capitals and descenders together), so a
    /// date doesn't change size with its month; a text too wide for the area is reduced. Drawn as an outline, because the
    /// picture is transparent, where the colour fringes of normal screen text would stay visible.
    /// </summary>
    private static void DrawField(DrawingContext drawing, StampField field, string text, PixelSize picture)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var brush = new SolidColorBrush(Color.TryParse(field.Color, out var color) ? color : Colors.Black);
        if (Outline(text) is not { } outline || Outline(text.ToUpperInvariant() == text ? "H" : "Hg") is not { } reference)
            return;
        var area = new Rect(field.X * picture.Width, field.Y * picture.Height, field.Width * picture.Width, field.Height * picture.Height);
        var ink = outline.Bounds;
        var line = reference.Bounds;
        var scale = area.Height / Math.Max(1, line.Height);
        if (ink.Width * scale > area.Width)
            scale = area.Width / Math.Max(1, ink.Width);
        var x = field.Align switch
        {
            StampFieldAlign.Left => area.X,
            StampFieldAlign.Right => area.Right - ink.Width * scale,
            _ => area.X + (area.Width - ink.Width * scale) / 2,
        };
        var y = area.Y + area.Height / 2 - (line.Y + line.Height / 2) * scale;
        using (drawing.PushTransform(Matrix.CreateTranslation(-ink.X, 0) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y)))
            drawing.DrawGeometry(brush, null, outline);
    }

    private static Geometry? Outline(string text) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, OutlineSize, Brushes.Black).BuildGeometry(new Point(0, 0));
}
