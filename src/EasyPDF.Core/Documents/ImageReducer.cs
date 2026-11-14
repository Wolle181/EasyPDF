using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Pdfium;
using PDFiumCore;
using SkiaSharp;

namespace EasyPDF.Core.Documents;

/// <summary>Settings for making a PDF smaller by scaling down its scans and pictures when saving.</summary>
/// <param name="MaxDpi">Pictures shown with more pixels per inch than this are scaled down to this resolution.</param>
/// <param name="JpegQuality">JPEG quality (1–100) of the scaled-down pictures.</param>
public sealed record ImageReduction(int MaxDpi, int JpegQuality)
{
    public static ImageReduction High { get; } = new(200, 85);
    public static ImageReduction Medium { get; } = new(150, 75);
    public static ImageReduction Small { get; } = new(100, 60);
}

/// <summary>
/// Scales down the pictures of a PDF file that pdfium has just written. The page contents are never generated again
/// (pdfium does not reproduce every content faithfully when it does): pdfium finds the pictures and where they are
/// shown, Skia scales them and encodes them as JPEG, the new pictures are appended as an incremental update under the
/// object numbers of the old ones, and pdfium then writes the whole file once more, which leaves the old data behind.
/// A picture used on several pages or inside form objects is one object and is replaced once.
/// Left alone: pictures with a mask or transparency (a JPEG would lose it), 1-bit scans (CCITT/JBIG2 are smaller than
/// any JPEG), indexed and spot colour pictures (graphics, print colours), small ones, ones whose JPEG would not be
/// smaller, and encrypted files.
/// </summary>
internal static class ImageReducer
{
    // Pictures only a little above the target stay as they are: re-encoding them costs quality for little gain
    private const double Tolerance = 1.25;
    private const int MinSide = 64;

    // fpdf_edit.h: page object types and colour spaces
    private const int ObjectImage = 3, ObjectForm = 5;
    private const int DeviceGray = 1, DeviceRgb = 2, DeviceCmyk = 3, CalGray = 4, CalRgb = 5, Lab = 6, IccBased = 7;

    /// <summary>One picture of the file (one image stream, recognized by its data), wherever it is shown.</summary>
    private sealed class Picture(int page, int[] path, int width, int height, long rawSize, bool keep)
    {
        /// <summary>Where it is shown first: page and object indices from the page down through form objects.</summary>
        public int Page { get; } = page;
        public int[] Path { get; } = path;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public long RawSize { get; } = rawSize;

        /// <summary>The picture must stay as it is (mask, unsuitable colour space, too small).</summary>
        public bool Keep { get; set; } = keep;

        /// <summary>The lowest resolution it is shown at; that place decides how far it can be scaled down.</summary>
        public double MinDpi { get; set; } = double.MaxValue;

        public byte[]? Jpeg { get; set; }
        public int NewWidth { get; set; }
        public int NewHeight { get; set; }
        public bool Gray { get; set; }
    }

    private sealed record Pixels(byte[] Data, int Width, int Height, int Stride, int Format);

    /// <summary>An image stream in the file: object number, generation and dictionary text.</summary>
    private sealed record ImageObject(int Number, int Generation, string Dictionary, string Hash);

    /// <summary>
    /// Scales down the pictures of the PDF at <paramref name="path"/> (replaced in place); returns how many pictures were.
    /// </summary>
    public static async Task<int> ReduceFileAsync(PdfiumWorker worker, string path, ImageReduction settings,
        IProgress<double>? progress, CancellationToken token)
    {
        // 1. Find the pictures, where they are shown and at what resolution, then scale and encode the ones above the target
        var pictures = new Dictionary<string, Picture>();
        await using (var source = await PdfSource.OpenAsync(path, worker, token).ConfigureAwait(false))
        {
            for (var i = 0; i < source.PageCount; i++)
            {
                var index = i;
                await worker.Run(() => ScanPage(source.Handle, index, settings.MaxDpi, pictures), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
                progress?.Report(0.3 * (i + 1) / source.PageCount);
            }

            var chosen = pictures.Values.Where(p => !p.Keep && p.MinDpi > settings.MaxDpi * Tolerance).ToList();
            for (var i = 0; i < chosen.Count; i++)
            {
                var picture = chosen[i];
                var pixels = await worker.Run(() => ReadPixels(source.Handle, picture), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
                if (pixels != null)
                {
                    var factor = settings.MaxDpi / picture.MinDpi;
                    picture.NewWidth = Math.Max(1, (int)Math.Round(picture.Width * factor));
                    picture.NewHeight = Math.Max(1, (int)Math.Round(picture.Height * factor));
                    picture.Gray = pixels.Format == (int)FPDFBitmapFormat.Gray;
                    // Scaling and encoding run beside the pdfium thread
                    var jpeg = await Task.Run(() => Encode(pixels, picture.NewWidth, picture.NewHeight, settings.JpegQuality), token).ConfigureAwait(false);
                    if (jpeg != null && jpeg.Length < picture.RawSize)
                        picture.Jpeg = jpeg;
                }
                progress?.Report(0.3 + 0.5 * (i + 1) / chosen.Count);
            }
        }
        if (!pictures.Values.Any(p => p.Jpeg != null))
            return 0;

        // 2. Append the new pictures to the file under the old object numbers
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        var replace = FindImageObjects(bytes).Where(o => pictures.TryGetValue(o.Hash, out var p) && p.Jpeg != null).ToList();
        if (replace.Count == 0)
            return 0;
        var updated = $"{path}.{Guid.NewGuid():N}.tmp";
        var rewritten = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (!WriteUpdate(bytes, replace, pictures, updated))
                return 0;
            token.ThrowIfCancellationRequested();

            // 3. pdfium writes the whole file again: only the current version of each object is written
            await using (var reduced = await PdfSource.OpenAsync(updated, worker, token).ConfigureAwait(false))
                await worker.Run(() => PdfSaver.SaveToFile(reduced.Handle, rewritten), PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(rewritten, path, overwrite: true);
            progress?.Report(1);
            return replace.Select(o => o.Hash).Distinct().Count();
        }
        finally
        {
            TryDelete(updated);
            TryDelete(rewritten);
        }
    }

    #region pdfium (worker thread only)

    private static void ScanPage(FpdfDocumentT doc, int pageIndex, int maxDpi, Dictionary<string, Picture> pictures)
    {
        var page = fpdfview.FPDF_LoadPage(doc, pageIndex) ?? throw new InvalidOperationException($"Page {pageIndex + 1} could not be loaded.");
        try
        {
            // parent: the 2x2 part of the matrix from the parent's space to page space (row vectors, as in PDF)
            void Visit(FpdfPageobjectT obj, int[] path, (double A, double B, double C, double D) parent)
            {
                var type = fpdf_edit.FPDFPageObjGetType(obj);
                if (type is not (ObjectImage or ObjectForm))
                    return;
                using var m = new FS_MATRIX_();
                if (fpdf_edit.FPDFPageObjGetMatrix(obj, m) == 0)
                    return;
                var total = (A: m.A * parent.A + m.B * parent.C, B: m.A * parent.B + m.B * parent.D,
                             C: m.C * parent.A + m.D * parent.C, D: m.C * parent.B + m.D * parent.D);
                if (type == ObjectForm)
                {
                    var children = fpdf_edit.FPDFFormObjCountObjects(obj);
                    for (var i = 0; i < children; i++)
                        Visit(fpdf_edit.FPDFFormObjGetObject(obj, (ulong)i), [.. path, i], total);
                }
                else
                    AddPlacement(doc, page, pageIndex, obj, path, total, maxDpi, pictures);
            }

            var count = fpdf_edit.FPDFPageCountObjects(page);
            for (var i = 0; i < count; i++)
                Visit(fpdf_edit.FPDFPageGetObject(page, i), [i], (1, 0, 0, 1));
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>Records one place a picture is shown: its resolution there, and whether it has to stay as it is.</summary>
    private static void AddPlacement(FpdfDocumentT doc, FpdfPageT page, int pageIndex, FpdfPageobjectT obj, int[] path,
        (double A, double B, double C, double D) matrix, int maxDpi, Dictionary<string, Picture> pictures)
    {
        var raw = RawData(obj);
        if (raw == null)
            return;
        var hash = Convert.ToHexString(SHA256.HashData(raw));
        if (!pictures.TryGetValue(hash, out var picture))
        {
            using var meta = new FPDF_IMAGEOBJ_METADATA();
            var known = fpdf_edit.FPDFImageObjGetImageMetadata(obj, page, meta) != 0;
            int width = (int)meta.Width, height = (int)meta.Height;
            var keep = !known || meta.BitsPerPixel < 8 || width < MinSide || height < MinSide || !IsPlainColorSpace(meta.Colorspace);
            pictures[hash] = picture = new Picture(pageIndex, path, width, height, raw.Length, keep);
        }
        if (picture.Keep)
            return;

        // The unit square of a picture is its pixel grid: the matrix rows are its shown width and height in points
        var shownWidth = Math.Sqrt(matrix.A * matrix.A + matrix.B * matrix.B) / 72;
        var shownHeight = Math.Sqrt(matrix.C * matrix.C + matrix.D * matrix.D) / 72;
        if (shownWidth <= 0 || shownHeight <= 0)
            return;
        var dpi = Math.Min(picture.Width / shownWidth, picture.Height / shownHeight);
        picture.MinDpi = Math.Min(picture.MinDpi, dpi);
        // The mask is a property of the picture; checking it where it would be reduced is enough
        if (dpi > maxDpi * Tolerance && (fpdf_edit.FPDFPageObjHasTransparency(obj) != 0 || HasMask(doc, page, obj)))
            picture.Keep = true;
    }

    private static byte[]? RawData(FpdfPageobjectT obj)
    {
        var size = (long)fpdf_edit.FPDFImageObjGetImageDataRaw(obj, IntPtr.Zero, 0);
        if (size <= 0)
            return null;
        var raw = new byte[size];
        var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
        try
        {
            return fpdf_edit.FPDFImageObjGetImageDataRaw(obj, handle.AddrOfPinnedObject(), (ulong)size) == (ulong)size ? raw : null;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Colour spaces a JPEG can stand in for (no palettes, patterns or spot colours).</summary>
    private static bool IsPlainColorSpace(int colorSpace) =>
        colorSpace is DeviceGray or DeviceRgb or DeviceCmyk or CalGray or CalRgb or Lab or IccBased;

    /// <summary>
    /// Whether the picture has a mask (soft mask, colour key or stencil). pdfium has no call for that, so the picture is
    /// rendered with its mask and checked for see-through pixels (the outermost pixels are left out, they can be partly
    /// covered at the picture's edge). A picture pdfium cannot render this way (e.g. turned by its matrix) counts as
    /// masked and stays as it is.
    /// </summary>
    private static bool HasMask(FpdfDocumentT doc, FpdfPageT page, FpdfPageobjectT obj)
    {
        var bitmap = fpdf_edit.FPDFImageObjGetRenderedBitmap(doc, page, obj);
        if (bitmap == null)
            return true;
        try
        {
            if (fpdfview.FPDFBitmapGetFormat(bitmap) != (int)FPDFBitmapFormat.BGRA)
                return false;
            int w = fpdfview.FPDFBitmapGetWidth(bitmap), h = fpdfview.FPDFBitmapGetHeight(bitmap), stride = fpdfview.FPDFBitmapGetStride(bitmap);
            const int edge = 2;
            var row = new byte[stride];
            var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
            for (var y = edge; y < h - edge; y++)
            {
                Marshal.Copy(buffer + y * stride, row, 0, stride);
                for (var x = edge; x < w - edge; x++)
                    if (row[x * 4 + 3] < 255)
                        return true;
            }
            return false;
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }

    /// <summary>The decoded pixels of a picture (found again by its page and object path); null if pdfium cannot give them.</summary>
    private static Pixels? ReadPixels(FpdfDocumentT doc, Picture picture)
    {
        var page = fpdfview.FPDF_LoadPage(doc, picture.Page);
        if (page == null)
            return null;
        try
        {
            var obj = fpdf_edit.FPDFPageGetObject(page, picture.Path[0]);
            foreach (var index in picture.Path.Skip(1))
                obj = obj == null ? null : fpdf_edit.FPDFFormObjGetObject(obj, (ulong)index);
            if (obj == null)
                return null;
            var bitmap = fpdf_edit.FPDFImageObjGetBitmap(obj);
            if (bitmap == null)
                return null;
            try
            {
                var format = fpdfview.FPDFBitmapGetFormat(bitmap);
                int w = fpdfview.FPDFBitmapGetWidth(bitmap), h = fpdfview.FPDFBitmapGetHeight(bitmap), stride = fpdfview.FPDFBitmapGetStride(bitmap);
                if (w != picture.Width || h != picture.Height || format is < (int)FPDFBitmapFormat.Gray or > (int)FPDFBitmapFormat.BGRA)
                    return null;
                var data = new byte[(long)stride * h];
                Marshal.Copy(fpdfview.FPDFBitmapGetBuffer(bitmap), data, 0, data.Length);
                return new Pixels(data, w, h, stride, format);
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
            }
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    #endregion

    /// <summary>The picture scaled to the given size as JPEG (grey pictures stay grey); null if Skia cannot do it.</summary>
    private static byte[]? Encode(Pixels p, int width, int height, int quality)
    {
        var gray = p.Format == (int)FPDFBitmapFormat.Gray;
        var colorType = gray ? SKColorType.Gray8 : SKColorType.Bgra8888;
        using var source = new SKBitmap(new SKImageInfo(p.Width, p.Height, colorType, SKAlphaType.Opaque));
        var target = source.GetPixelSpan();
        var rowBytes = source.RowBytes;
        for (var y = 0; y < p.Height; y++)
        {
            var row = p.Data.AsSpan(y * p.Stride);
            var dest = target.Slice(y * rowBytes);
            if (p.Format == (int)FPDFBitmapFormat.BGR)
                for (var x = 0; x < p.Width; x++)
                {
                    dest[x * 4] = row[x * 3];
                    dest[x * 4 + 1] = row[x * 3 + 1];
                    dest[x * 4 + 2] = row[x * 3 + 2];
                    dest[x * 4 + 3] = 255;
                }
            else
            {
                row[..(gray ? p.Width : p.Width * 4)].CopyTo(dest);
                if (!gray)
                    for (var x = 3; x < p.Width * 4; x += 4)
                        dest[x] = 255; // BGRx has no defined fourth byte
            }
        }
        using var scaled = source.Resize(new SKImageInfo(width, height, colorType, SKAlphaType.Opaque),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (scaled == null)
            return null;
        using var image = SKImage.FromBitmap(scaled);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data?.ToArray();
    }

    #region PDF file structure

    private static readonly Regex ObjectStart = new(@"(?<![0-9])(\d+)\s+(\d+)\s+obj\b", RegexOptions.Compiled);
    private static readonly Regex SubtypeImage = new(@"/Subtype\s*/Image(?![A-Za-z0-9])", RegexOptions.Compiled);
    private static readonly Regex DirectLength = new(@"/Length\s+(\d+)(?!\s+\d+\s+R)(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex Prev = new(@"/Prev\s+\d+", RegexOptions.Compiled);
    // Entries of the old picture's dictionary that do not describe its data and are kept
    private static readonly Regex KeptEntries = new(@"/(OC|Metadata)\s+\d+\s+\d+\s+R|/StructParent\s+\d+|/Interpolate\s*(true|false)", RegexOptions.Compiled);

    /// <summary>
    /// The image streams of a file as pdfium writes it (plain objects and a classic cross-reference table, no object
    /// streams), with the hash of their data, which is what pdfium reports as their raw data.
    /// </summary>
    private static List<ImageObject> FindImageObjects(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var found = new List<ImageObject>();
        foreach (Match m in ObjectStart.Matches(text))
        {
            var start = SkipWhitespace(text, m.Index + m.Length);
            if (!text.AsSpan(start).StartsWith("<<"))
                continue;
            var end = DictionaryEnd(text, start);
            if (end < 0)
                continue;
            var dict = text[start..end];
            if (!SubtypeImage.IsMatch(dict) || DirectLength.Match(dict) is not { Success: true } length)
                continue;
            var data = SkipWhitespace(text, end);
            if (!text.AsSpan(data).StartsWith("stream"))
                continue;
            data += "stream".Length;
            data += text[data] == '\r' && data + 1 < text.Length && text[data + 1] == '\n' ? 2 : 1;
            var size = int.Parse(length.Groups[1].Value);
            if (data + size > bytes.Length)
                continue;
            found.Add(new ImageObject(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), dict,
                Convert.ToHexString(SHA256.HashData(bytes.AsSpan(data, size)))));
        }
        return found;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> plus an incremental update with the new pictures to <paramref name="path"/>;
    /// false if the file has no classic trailer or is encrypted.
    /// </summary>
    private static bool WriteUpdate(byte[] bytes, List<ImageObject> replace, Dictionary<string, Picture> pictures, string path)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var startXref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var trailerAt = startXref < 0 ? -1 : text.LastIndexOf("trailer", startXref, StringComparison.Ordinal);
        if (trailerAt < 0)
            return false;
        var prevMatch = Regex.Match(text[(startXref + "startxref".Length)..], @"^\s*(\d+)");
        var dictStart = SkipWhitespace(text, trailerAt + "trailer".Length);
        var dictEnd = text.AsSpan(dictStart).StartsWith("<<") ? DictionaryEnd(text, dictStart) : -1;
        if (!prevMatch.Success || dictEnd < 0)
            return false;
        var trailer = text[dictStart..dictEnd];
        if (trailer.Contains("/Encrypt"))
            return false;
        trailer = "<</Prev " + prevMatch.Groups[1].Value + Prev.Replace(trailer, "")[2..];

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        void Write(string s) => file.Write(Encoding.Latin1.GetBytes(s));
        file.Write(bytes);
        Write("\n");
        var offsets = new List<(int Number, int Generation, long Offset)>();
        foreach (var obj in replace)
        {
            var picture = pictures[obj.Hash];
            var jpeg = picture.Jpeg!;
            var kept = string.Concat(KeptEntries.Matches(obj.Dictionary).Select(k => k.Value));
            offsets.Add((obj.Number, obj.Generation, file.Position));
            Write($"{obj.Number} {obj.Generation} obj\n<</Type/XObject/Subtype/Image/Width {picture.NewWidth}/Height {picture.NewHeight}" +
                  $"/ColorSpace/{(picture.Gray ? "DeviceGray" : "DeviceRGB")}/BitsPerComponent 8/Filter/DCTDecode{kept}/Length {jpeg.Length}>>\nstream\n");
            file.Write(jpeg);
            Write("\nendstream\nendobj\n");
        }
        var xref = file.Position;
        var table = new StringBuilder("xref\n");
        foreach (var (number, generation, offset) in offsets.OrderBy(o => o.Number))
            table.Append($"{number} 1\n{offset:D10} {generation:D5} n\r\n");
        Write($"{table}trailer\n{trailer}\nstartxref\n{xref}\n%%EOF\n");
        return true;
    }

    private static int SkipWhitespace(string text, int i)
    {
        while (i < text.Length && text[i] is ' ' or '\r' or '\n' or '\t' or '\f' or '\0')
            i++;
        return i;
    }

    /// <summary>Index just after the dictionary that starts with "&lt;&lt;" at <paramref name="start"/>; -1 if it does not end.</summary>
    private static int DictionaryEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(')
            {
                // Literal string: balanced parentheses, backslash escapes
                var nesting = 0;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '\\')
                        i++;
                    else if (text[i] == '(')
                        nesting++;
                    else if (text[i] == ')' && --nesting == 0)
                        break;
                }
            }
            else if (c == '<' && i + 1 < text.Length && text[i + 1] == '<')
            {
                depth++;
                i++;
            }
            else if (c == '>' && i + 1 < text.Length && text[i + 1] == '>')
            {
                i++;
                if (--depth == 0)
                    return i + 1;
            }
            else if (c == '<')
            {
                // Hex string
                var close = text.IndexOf('>', i);
                if (close < 0)
                    return -1;
                i = close;
            }
        }
        return -1;
    }

    #endregion

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
