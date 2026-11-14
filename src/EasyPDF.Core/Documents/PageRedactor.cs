using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;
using PDFiumCore;
using SkiaSharp;

namespace EasyPDF.Core.Documents;

/// <summary>
/// Applies the redactions of one page of a document being written (worker thread only). Every page object that reaches
/// into a box is removed: texts, pictures, drawings and form objects, also invisible text. What they showed outside the
/// boxes comes back as a picture of their area (rendered before they are removed), so the page looks the same apart
/// from the boxes, and their characters outside the boxes come back as invisible text, so it can still be searched.
/// Objects that do not reach into a box stay as they are (sharp drawings, real text). The boxes themselves are written
/// as filled rectangles on top; annotations that reach into a box are removed, form field values cleared.
/// pdfium writes only objects that are still used, so the removed content is not in the saved file at all
/// (RedactionSaveTests look for it in the raw file).
/// </summary>
internal static class PageRedactor
{
    private const double Dpi = 300;
    private const double MinPictureDpi = 150;
    private const double MaxPatchPixels = 40e6; // about A3 at 300 dpi; larger areas get a lower resolution
    private const int JpegQuality = 90;
    private const double WholePageShare = 0.6;  // pictures covering more of the page than this become one for the whole page
    private const double PatchMargin = 1;       // pt around an object's bounds: its anti-aliased edge
    private const int ObjectText = 1, ObjectImage = 3, ObjectForm = 5;
    private const int AnnotWidget = 20, AppearanceNormal = 0;
    private const int FillAlternate = 1;

    public static void Apply(FpdfDocumentT doc, int index, PageEntry entry)
    {
        var areas = entry.RemovalAreas;
        if (areas.Count == 0)
            return;
        var page = fpdfview.FPDF_LoadPage(doc, index) ?? throw new InvalidOperationException($"Page {index + 1} could not be loaded.");
        try
        {
            var size = entry.BaseSize;
            var pageRect = new RectPt(0, 0, size.Width, size.Height);
            var boxes = areas
                .Select(r => r with { Rect = Clip(r.Rect, pageRect) })
                .Where(r => r.Rect.Width > 0 && r.Rect.Height > 0)
                .ToList();

            // 1. The objects that reach into a box
            var removed = new List<(FpdfPageobjectT Object, RectPt Bounds)>();
            var count = fpdf_edit.FPDFPageCountObjects(page);
            for (var i = 0; i < count; i++)
            {
                var obj = fpdf_edit.FPDFPageGetObject(page, i);
                if (obj != null && Bounds(page, size, obj) is { } bounds && Redaction.Covers(boxes, bounds))
                    removed.Add((obj, bounds));
            }

            // 2. Before they go: their characters outside the boxes, and pictures of what they showed there
            var words = removed.Any(r => fpdf_edit.FPDFPageObjGetType(r.Object) == ObjectText)
                ? VisibleCharacters(page, size, [.. removed.Select(r => r.Object.__Instance)], boxes)
                : [];
            var patches = new List<(RectPt Rect, FpdfBitmapT Bitmap, bool Photo)>();
            try
            {
                foreach (var area in PatchAreas([.. removed.Select(r => r.Bounds)], pageRect))
                {
                    var inside = removed.Where(r => Redaction.Intersects(r.Bounds, area)).ToList();
                    var photo = inside.Any(r => ContainsImage(r.Object));
                    var (rect, bitmap) = Render(page, size, area, boxes, photo ? PictureDpi(inside) : Dpi);
                    patches.Add((rect, bitmap, photo));
                }

                // 3. Remove the objects, put the pictures, the invisible text and the boxes in their place
                foreach (var (obj, _) in removed)
                {
                    if (fpdf_edit.FPDFPageRemoveObject(page, obj) == 0)
                        throw new InvalidOperationException($"Content of page {index + 1} could not be removed.");
                    fpdf_edit.FPDFPageObjDestroy(obj);
                }
                foreach (var (rect, bitmap, photo) in patches)
                    InsertPicture(doc, page, size, rect, bitmap, photo);
            }
            finally
            {
                foreach (var patch in patches)
                    fpdfview.FPDFBitmapDestroy(patch.Bitmap);
            }
            foreach (var word in words)
                PdfSaver.AddInvisibleWord(doc, page, size, word);
            foreach (var box in boxes)
                InsertBox(page, size, box);

            RemoveAnnotations(page, size, boxes);
            if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
                throw new InvalidOperationException($"The redactions of page {index + 1} could not be written.");
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private static RectPt Clip(RectPt r, RectPt to)
    {
        double x0 = Math.Max(r.X, to.X), y0 = Math.Max(r.Y, to.Y), x1 = Math.Min(r.Right, to.Right), y1 = Math.Min(r.Bottom, to.Bottom);
        return new RectPt(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    private static RectPt Union(RectPt a, RectPt b)
    {
        double x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        return new RectPt(x0, y0, Math.Max(a.Right, b.Right) - x0, Math.Max(a.Bottom, b.Bottom) - y0);
    }

    #region Page space

    private const double DeviceScale = 100; // device units per point, for FPDF_PageToDevice's integer results

    /// <summary>A point of user space in page space (after /Rotate, origin top-left, points).</summary>
    private static (double X, double Y) ToPage(FpdfPageT page, PageSizePt size, double x, double y)
    {
        int dx = 0, dy = 0;
        fpdfview.FPDF_PageToDevice(page, 0, 0, (int)Math.Round(size.Width * DeviceScale), (int)Math.Round(size.Height * DeviceScale), 0, x, y, ref dx, ref dy);
        return (dx / DeviceScale, dy / DeviceScale);
    }

    /// <summary>A rect of user space in page space; two opposite corners are enough, /Rotate turns in steps of 90°.</summary>
    private static RectPt ToPage(FpdfPageT page, PageSizePt size, double left, double bottom, double right, double top)
    {
        var a = ToPage(page, size, left, top);
        var b = ToPage(page, size, right, bottom);
        return new RectPt(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    private static RectPt? Bounds(FpdfPageT page, PageSizePt size, FpdfPageobjectT obj)
    {
        float left = 0, bottom = 0, right = 0, top = 0;
        return fpdf_edit.FPDFPageObjGetBounds(obj, ref left, ref bottom, ref right, ref top) != 0
            ? ToPage(page, size, left, bottom, right, top)
            : null;
    }

    #endregion

    /// <summary>A picture inside the object (itself or inside a form object): its area becomes a JPEG instead of a lossless picture.</summary>
    private static bool ContainsImage(FpdfPageobjectT obj)
    {
        var type = fpdf_edit.FPDFPageObjGetType(obj);
        if (type == ObjectImage)
            return true;
        if (type != ObjectForm)
            return false;
        var count = fpdf_edit.FPDFFormObjCountObjects(obj);
        for (var i = 0; i < count; i++)
            if (fpdf_edit.FPDFFormObjGetObject(obj, (ulong)i) is { } child && ContainsImage(child))
                return true;
        return false;
    }

    /// <summary>
    /// Resolution for an area showing pictures: that of the sharpest picture placed directly on the page (a scan stays
    /// at its own resolution instead of growing), at least <see cref="MinPictureDpi"/>, at most <see cref="Dpi"/>.
    /// </summary>
    private static double PictureDpi(List<(FpdfPageobjectT Object, RectPt Bounds)> objects)
    {
        var dpi = 0.0;
        foreach (var (obj, bounds) in objects)
        {
            var type = fpdf_edit.FPDFPageObjGetType(obj);
            if (type == ObjectForm && ContainsImage(obj))
                return Dpi; // a picture inside a form object: its resolution is not known here
            if (type != ObjectImage)
                continue;
            uint w = 0, h = 0;
            if (fpdf_edit.FPDFImageObjGetImagePixelSize(obj, ref w, ref h) == 0 || bounds.Width <= 0 || bounds.Height <= 0)
                return Dpi;
            dpi = Math.Max(dpi, Math.Max(w / (bounds.Width / 72), h / (bounds.Height / 72)));
        }
        return Math.Clamp(dpi, MinPictureDpi, Dpi);
    }

    /// <summary>
    /// The areas to put back as pictures: the removed objects' bounds with a small margin, overlapping ones joined;
    /// one area for the whole page if they cover most of it anyway.
    /// </summary>
    private static List<RectPt> PatchAreas(List<RectPt> bounds, RectPt pageRect)
    {
        var areas = bounds
            .Select(b => Clip(new RectPt(b.X - PatchMargin, b.Y - PatchMargin, b.Width + 2 * PatchMargin, b.Height + 2 * PatchMargin), pageRect))
            .Where(a => a.Width > 0 && a.Height > 0)
            .ToList();
        for (var joined = true; joined;)
        {
            joined = false;
            for (var i = 0; i < areas.Count && !joined; i++)
                for (var j = i + 1; j < areas.Count; j++)
                    if (Redaction.Intersects(areas[i], areas[j]))
                    {
                        areas[i] = Union(areas[i], areas[j]);
                        areas.RemoveAt(j);
                        joined = true;
                        break;
                    }
        }
        var covered = areas.Sum(a => a.Width * a.Height);
        return covered > WholePageShare * pageRect.Width * pageRect.Height ? [pageRect] : areas;
    }

    /// <summary>
    /// Renders an area of the page (without annotations, they stay what they are) with the boxes painted in. Returns the
    /// rect the pixels cover exactly (the area rounded out to whole pixels) and the bitmap (BGRx).
    /// </summary>
    private static (RectPt Rect, FpdfBitmapT Bitmap) Render(FpdfPageT page, PageSizePt size, RectPt area, List<RedactionOverlay> boxes, double dpi)
    {
        var scale = dpi / 72;
        var pixels = area.Width * scale * area.Height * scale;
        if (pixels > MaxPatchPixels)
            scale *= Math.Sqrt(MaxPatchPixels / pixels);
        int x0 = (int)Math.Floor(area.X * scale), y0 = (int)Math.Floor(area.Y * scale);
        int x1 = Math.Max(x0 + 1, (int)Math.Ceiling(area.Right * scale)), y1 = Math.Max(y0 + 1, (int)Math.Ceiling(area.Bottom * scale));
        int w = x1 - x0, h = y1 - y0;
        var bitmap = fpdfview.FPDFBitmapCreate(w, h, 0) ?? throw new OutOfMemoryException("Bitmap allocation failed.");
        fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
        fpdfview.FPDF_RenderPageBitmap(bitmap, page, -x0, -y0, (int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale), 0, 0);
        // The pixels under a box must not keep what it hides, even where the box on top would cover them
        foreach (var box in boxes)
        {
            var bx0 = Math.Max(0, (int)Math.Floor(box.Rect.X * scale) - x0);
            var by0 = Math.Max(0, (int)Math.Floor(box.Rect.Y * scale) - y0);
            var bx1 = Math.Min(w, (int)Math.Ceiling(box.Rect.Right * scale) - x0);
            var by1 = Math.Min(h, (int)Math.Ceiling(box.Rect.Bottom * scale) - y0);
            if (bx1 > bx0 && by1 > by0)
                fpdfview.FPDFBitmapFillRect(bitmap, bx0, by0, bx1 - bx0, by1 - by0, box.Fill == RedactionFill.White ? 0xFFFFFFFFUL : 0xFF000000UL);
        }
        return (new RectPt(x0 / scale, y0 / scale, w / scale, h / scale), bitmap);
    }

    /// <summary>Puts a rendered area onto the page as a picture object on top (lossless, or JPEG where it shows pictures).</summary>
    private static void InsertPicture(FpdfDocumentT doc, FpdfPageT page, PageSizePt size, RectPt rect, FpdfBitmapT bitmap, bool photo)
    {
        var image = fpdf_edit.FPDFPageObjNewImageObj(doc) ?? throw new InvalidOperationException("The picture could not be created.");
        var jpeg = photo ? EncodeJpeg(bitmap) : null;
        if (jpeg != null)
            LoadJpeg(image, jpeg);
        else if (fpdf_edit.FPDFImageObjSetBitmap(null!, 0, image, bitmap) == 0)
        {
            fpdf_edit.FPDFPageObjDestroy(image);
            throw new InvalidOperationException("The picture could not be set.");
        }
        // Unit square of the picture: (0,0) bottom-left, (1,0) bottom-right, (0,1) top-left
        var tl = PdfSaver.ToUser(page, size, rect.X, rect.Y);
        var br = PdfSaver.ToUser(page, size, rect.Right, rect.Bottom);
        var bl = PdfSaver.ToUser(page, size, rect.X, rect.Bottom);
        if (fpdf_edit.FPDFImageObjSetMatrix(image, br.X - bl.X, br.Y - bl.Y, tl.X - bl.X, tl.Y - bl.Y, bl.X, bl.Y) == 0)
        {
            fpdf_edit.FPDFPageObjDestroy(image);
            throw new InvalidOperationException("The picture could not be positioned.");
        }
        fpdf_edit.FPDFPageInsertObject(page, image);
    }

    private static byte[]? EncodeJpeg(FpdfBitmapT bitmap)
    {
        int w = fpdfview.FPDFBitmapGetWidth(bitmap), h = fpdfview.FPDFBitmapGetHeight(bitmap), stride = fpdfview.FPDFBitmapGetStride(bitmap);
        using var skia = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var target = skia.GetPixelSpan();
        var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
        var row = new byte[w * 4];
        for (var y = 0; y < h; y++)
        {
            Marshal.Copy(buffer + y * stride, row, 0, row.Length);
            for (var x = 3; x < row.Length; x += 4)
                row[x] = 255; // BGRx has no defined fourth byte
            row.CopyTo(target.Slice(y * skia.RowBytes));
        }
        using var image = SKImage.FromBitmap(skia);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data?.ToArray();
    }

    private static unsafe void LoadJpeg(FpdfPageobjectT image, byte[] jpeg)
    {
        PDFiumCore.Delegates.Func_int___IntPtr_ulong_bytePtr_ulong getBlock = (_, position, buffer, size) =>
        {
            if (position + size > (ulong)jpeg.Length)
                return 0;
            jpeg.AsSpan((int)position, (int)size).CopyTo(new Span<byte>(buffer, (int)size));
            return 1;
        };
        var access = new FPDF_FILEACCESS { MFileLen = (ulong)jpeg.Length, MGetBlock = getBlock };
        // "Inline": the data is read at once, the callback is not needed afterwards
        var loaded = fpdf_edit.FPDFImageObjLoadJpegFileInline(null!, 0, image, access);
        GC.KeepAlive(getBlock);
        GC.KeepAlive(access);
        if (loaded == 0)
        {
            fpdf_edit.FPDFPageObjDestroy(image);
            throw new InvalidOperationException("The picture could not be set.");
        }
    }

    private static void InsertBox(FpdfPageT page, PageSizePt size, RedactionOverlay box)
    {
        var a = PdfSaver.ToUser(page, size, box.Rect.X, box.Rect.Y);
        var b = PdfSaver.ToUser(page, size, box.Rect.Right, box.Rect.Bottom);
        var rect = fpdf_edit.FPDFPageObjCreateNewRect((float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y), (float)Math.Abs(b.X - a.X), (float)Math.Abs(b.Y - a.Y))
            ?? throw new InvalidOperationException("The box could not be created.");
        var level = box.Fill == RedactionFill.White ? 255u : 0u;
        fpdf_edit.FPDFPageObjSetFillColor(rect, level, level, level, 255);
        fpdf_edit.FPDFPathSetDrawMode(rect, FillAlternate, 0);
        fpdf_edit.FPDFPageInsertObject(page, rect);
    }

    /// <summary>
    /// The characters of the removed text objects that lie outside all boxes, as words (split at white space, at removed
    /// characters and at line changes) in page space, for the invisible text layer. Their boxes are the loose ones
    /// (line height, like OCR boxes); whether a character is under a box decides its tight glyph box.
    /// </summary>
    private static List<OcrWord> VisibleCharacters(FpdfPageT page, PageSizePt size, HashSet<IntPtr> removed, List<RedactionOverlay> boxes)
    {
        var words = new List<OcrWord>();
        var text = fpdf_text.FPDFTextLoadPage(page);
        if (text == null)
            return words;
        try
        {
            var pageTurns = fpdf_edit.FPDFPageGetRotation(page);
            var sb = new StringBuilder();
            RectPt? box = null;
            var rotation = 0;
            void Flush()
            {
                if (sb.Length > 0 && box is { } b)
                    words.Add(new OcrWord(sb.ToString(), b, 1, rotation));
                sb.Clear();
                box = null;
            }

            var count = fpdf_text.FPDFTextCountChars(text);
            for (var i = 0; i < count; i++)
            {
                var owner = fpdf_text.FPDFTextGetTextObject(text, i);
                var u = fpdf_text.FPDFTextGetUnicode(text, i);
                if (owner == null || !removed.Contains(owner.__Instance) || fpdf_text.FPDFTextIsGenerated(text, i) == 1
                    || u <= 32 || u == 0xA0 || u == 0xFFFE || u == 0xFFFF)
                {
                    Flush();
                    continue;
                }
                if (u == 2) // soft hyphen at a line end
                    continue;
                double left = 0, right = 0, bottom = 0, top = 0;
                var glyph = fpdf_text.FPDFTextGetCharBox(text, i, ref left, ref right, ref bottom, ref top) != 0
                    ? ToPage(page, size, left, bottom, right, top)
                    : (RectPt?)null;
                using var loose = new FS_RECTF_();
                var line = fpdf_text.FPDFTextGetLooseCharBox(text, i, loose) != 0
                    ? ToPage(page, size, loose.Left, loose.Bottom, loose.Right, loose.Top)
                    : glyph;
                if (line is not { } charBox || Redaction.Covers(boxes, glyph is { Width: > 0, Height: > 0 } g ? g : charBox))
                {
                    Flush();
                    continue;
                }
                var turn = CharRotation(text, i, pageTurns);
                if (box is { } current && (turn != rotation || !SameLine(current, charBox, turn)))
                    Flush();
                rotation = turn;
                box = box is { } b ? Union(b, charBox) : charBox;
                sb.Append(char.ConvertFromUtf32((int)u));
            }
            Flush();
        }
        finally
        {
            fpdf_text.FPDFTextClosePage(text);
        }
        return words;
    }

    /// <summary>The character's writing direction as a clockwise turn in page space (0, 90, 180, 270).</summary>
    private static int CharRotation(FpdfTextpageT text, int index, int pageTurns)
    {
        using var m = new FS_MATRIX_();
        // Counter-clockwise angle in user space (y up); page space has y down and is turned by /Rotate
        var angle = fpdf_text.FPDFTextGetMatrix(text, index, m) != 0 ? Math.Atan2(m.B, m.A) * 180 / Math.PI : 0;
        var clockwise = pageTurns * 90 - angle;
        return (int)(((Math.Round(clockwise / 90) * 90) % 360 + 360) % 360);
    }

    /// <summary>The character continues the word: at the same height for horizontal text, the same column for turned text.</summary>
    private static bool SameLine(RectPt word, RectPt c, int rotation)
    {
        var (a0, a1, b0, b1) = rotation is 90 or 270 ? (word.X, word.Right, c.X, c.Right) : (word.Y, word.Bottom, c.Y, c.Bottom);
        var overlap = Math.Min(a1, b1) - Math.Max(a0, b0);
        return overlap >= Math.Min(a1 - a0, b1 - b0) * 0.5;
    }

    /// <summary>Annotations reaching into a box are removed; a form field also loses its value and its appearance.</summary>
    private static void RemoveAnnotations(FpdfPageT page, PageSizePt size, List<RedactionOverlay> boxes)
    {
        for (var i = fpdf_annot.FPDFPageGetAnnotCount(page) - 1; i >= 0; i--)
        {
            var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
            if (annot == null)
                continue;
            bool hit;
            try
            {
                using var r = new FS_RECTF_();
                hit = fpdf_annot.FPDFAnnotGetRect(annot, r) != 0 && Redaction.Covers(boxes, ToPage(page, size, r.Left, r.Bottom, r.Right, r.Top));
                if (hit && fpdf_annot.FPDFAnnotGetSubtype(annot) == AnnotWidget)
                {
                    // The field stays in the form (pdfium cannot take it out): it must not keep what was typed into it
                    ushort empty = 0;
                    fpdf_annot.FPDFAnnotSetStringValue(annot, "V", ref empty);
                    fpdf_annot.FPDFAnnotSetAP(annot, AppearanceNormal, ref System.Runtime.CompilerServices.Unsafe.NullRef<ushort>());
                }
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annot);
            }
            if (hit)
                fpdf_annot.FPDFPageRemoveAnnot(page, i);
        }
    }
}
