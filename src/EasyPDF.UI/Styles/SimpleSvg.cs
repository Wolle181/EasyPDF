using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace EasyPDF.App.Styles;

/// <summary>
/// Reads the simple SVG files of a brand (the company logo, written by tools/icon/trace_logo.py and make_icon.py) as a
/// vector image, so it stays sharp at every size and display scaling. Understands the viewBox and &lt;path d fill&gt;
/// elements (optionally fill-rule and transform="translate(x,y)"), nothing else.
/// </summary>
public static class SimpleSvg
{
    public static DrawingImage Load(Stream stream)
    {
        var root = XDocument.Load(stream).Root ?? throw new InvalidDataException("Empty SVG");
        var box = (root.Attribute("viewBox")?.Value ?? throw new InvalidDataException("SVG without viewBox"))
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var group = new DrawingGroup();
        // An invisible rectangle of the viewBox: the image has the size of the whole logo, margins included
        group.Children.Add(new GeometryDrawing { Geometry = new RectangleGeometry(new Rect(box[0], box[1], box[2], box[3])), Brush = Brushes.Transparent });
        foreach (var path in root.Descendants(root.Name.Namespace + "path"))
        {
            // SVG fills non-zero unless told otherwise, Avalonia's path syntax even-odd unless it starts with F1
            var evenOdd = path.Attribute("fill-rule")?.Value == "evenodd";
            var geometry = Geometry.Parse((evenOdd ? "F0 " : "F1 ") + path.Attribute("d")?.Value);
            if (path.Attribute("transform")?.Value is { } transform)
            {
                var xy = transform.Replace("translate(", "").TrimEnd(')').Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                geometry.Transform = new TranslateTransform(xy[0], xy.Length > 1 ? xy[1] : 0);
            }
            group.Children.Add(new GeometryDrawing { Geometry = geometry, Brush = new SolidColorBrush(Color.Parse(path.Attribute("fill")?.Value ?? "#000000")) });
        }
        return new DrawingImage(group);
    }
}
