using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Core.Tests;

/// <summary>Writes minimal PDFs by hand (pdfium cannot create bookmarks, and distinct page sizes make the order checkable).</summary>
internal static class TestPdf
{
    public static void Create(string path, IReadOnlyList<(int W, int H, int Rotate)> pages,
        IReadOnlyList<(string Title, int Page)>? outline = null)
    {
        var objects = new List<string>();
        int PageObj(int i) => 4 + i; // objects: 1 catalog, 2 pages, 3 outlines, 4.. pages, then bookmarks
        var outlineFirst = 4 + pages.Count;

        objects.Add($"<</Type/Catalog/Pages 2 0 R{(outline != null ? " /Outlines 3 0 R" : "")}>>");
        objects.Add($"<</Type/Pages/Kids[{string.Join(" ", pages.Select((_, i) => $"{PageObj(i)} 0 R"))}]/Count {pages.Count}>>");
        objects.Add(outline == null
            ? "<<>>"
            : $"<</Type/Outlines/First {outlineFirst} 0 R/Last {outlineFirst + outline.Count - 1} 0 R/Count {outline.Count}>>");
        foreach (var (w, h, rot) in pages)
            objects.Add($"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {w} {h}]/Rotate {rot}>>");
        if (outline != null)
            for (var i = 0; i < outline.Count; i++)
            {
                var prev = i > 0 ? $"/Prev {outlineFirst + i - 1} 0 R" : "";
                var next = i < outline.Count - 1 ? $"/Next {outlineFirst + i + 1} 0 R" : "";
                objects.Add($"<</Title({outline[i].Title})/Parent 3 0 R{prev}{next}/Dest[{PageObj(outline[i].Page)} 0 R/Fit]>>");
            }

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
    }

    /// <summary>
    /// Pages of the given size (points), each covered by the same JPEG picture (one shared image object, like a scan).
    /// <paramref name="imageExtras"/> goes into the image dictionary; <paramref name="extraObjects"/> are appended as
    /// objects 4 + 2·pageCount and on (e.g. a soft mask; each page has its own content stream). <paramref name="extraContent"/>
    /// is drawn after the picture, <paramref name="extraResources"/> goes into the page resources.
    /// </summary>
    public static void WithJpeg(string path, byte[] jpeg, int pixelWidth, int pixelHeight, bool gray, int pageWidth, int pageHeight,
        int pageCount = 1, string imageExtras = "", IReadOnlyList<string>? extraObjects = null, string extraContent = "", string extraResources = "")
    {
        var image = 3 + pageCount;
        var content = Encoding.ASCII.GetBytes($"q {pageWidth} 0 0 {pageHeight} 0 0 cm /Im1 Do Q {extraContent}");
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<</Type/Catalog/Pages 2 0 R>>"),
            Encoding.ASCII.GetBytes($"<</Type/Pages/Kids[{string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i} 0 R"))}]/Count {pageCount}>>"),
        };
        for (var i = 0; i < pageCount; i++)
            objects.Add(Encoding.ASCII.GetBytes(
                $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {pageWidth} {pageHeight}]/Resources<</XObject<</Im1 {image} 0 R>>{extraResources}>>/Contents {image + 1 + i} 0 R>>"));
        objects.Add([
            .. Encoding.ASCII.GetBytes($"<</Type/XObject/Subtype/Image/Width {pixelWidth}/Height {pixelHeight}/ColorSpace/{(gray ? "DeviceGray" : "DeviceRGB")}" +
                                       $"/BitsPerComponent 8/Filter/DCTDecode{imageExtras}/Length {jpeg.Length}>>\nstream\n"),
            .. jpeg, .. Encoding.ASCII.GetBytes("\nendstream"),
        ]);
        for (var i = 0; i < pageCount; i++)
            objects.Add([.. Encoding.ASCII.GetBytes($"<</Length {content.Length}>>\nstream\n"), .. content, .. Encoding.ASCII.GetBytes("\nendstream")]);
        foreach (var extra in extraObjects ?? [])
            objects.Add(Encoding.Latin1.GetBytes(extra));

        using var file = new MemoryStream();
        void Write(string s) => file.Write(Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Write($"{i + 1} 0 obj\n");
            file.Write(objects[i]);
            Write("\nendobj\n");
        }
        var xref = file.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, file.ToArray());
    }

    /// <summary>One page with the given files embedded (document-level attachments, /EmbeddedFiles name tree).</summary>
    public static void WithAttachments(string path, params (string Name, byte[] Data)[] files)
    {
        // objects: 1 catalog, 2 pages, 3 page, then per file a file specification and its stream
        var names = string.Join(" ", files.Select((f, i) => $"({f.Name}) {4 + 2 * i} 0 R"));
        var objects = new List<byte[]>
        {
            Encoding.Latin1.GetBytes($"<</Type/Catalog/Pages 2 0 R/Names<</EmbeddedFiles<</Names[{names}]>>>>>>"),
            Encoding.ASCII.GetBytes("<</Type/Pages/Kids[3 0 R]/Count 1>>"),
            Encoding.ASCII.GetBytes("<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 300]>>"),
        };
        for (var i = 0; i < files.Length; i++)
        {
            objects.Add(Encoding.Latin1.GetBytes($"<</Type/Filespec/F({files[i].Name})/UF({files[i].Name})/EF<</F {5 + 2 * i} 0 R>>>>"));
            objects.Add([.. Encoding.ASCII.GetBytes($"<</Type/EmbeddedFile/Length {files[i].Data.Length}>>\nstream\n"), .. files[i].Data,
                .. Encoding.ASCII.GetBytes("\nendstream")]);
        }
        using var file = new MemoryStream();
        void Write(string s) => file.Write(Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Write($"{i + 1} 0 obj\n");
            file.Write(objects[i]);
            Write("\nendobj\n");
        }
        var xref = file.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<</Size {objects.Count + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, file.ToArray());
    }

    /// <summary>Pages of width 100 + widthBase + 10·i and height 200: the width identifies the page.</summary>
    public static string Numbered(string dir, string name, int count, int widthBase = 0,
        IReadOnlyList<(string, int)>? outline = null)
    {
        var path = Path.Combine(dir, name);
        Create(path, [.. Enumerable.Range(0, count).Select(i => (100 + widthBase + 10 * i, 200, 0))], outline);
        return path;
    }

    /// <summary>Displayed page sizes (rounded, including /Rotate) of a PDF file.</summary>
    public static async Task<(int W, int H)[]> SizesAsync(string path)
    {
        await using var s = await PdfSource.OpenAsync(path);
        await s.LoadPageSizesAsync();
        return
        [
            .. Enumerable.Range(0, s.PageCount)
                .Select(i => ((int)Math.Round(s.GetPageSizeOrEstimate(i).Width),
                    (int)Math.Round(s.GetPageSizeOrEstimate(i).Height)))
        ];
    }

    public static async Task<int[]> WidthsAsync(string path) => [.. (await SizesAsync(path)).Select(s => s.W)];
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "easypdf-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}
