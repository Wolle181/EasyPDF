using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;
using SkiaSharp;
using Xunit;

namespace EasyPDF.Core.Tests;

/// <summary>
/// Blacking out and covering: the boxes are undoable edits, and saving removes what lies under them from the file
/// (text, pictures, annotations, form values), not just hides it, while the rest of the page stays as it was.
/// </summary>
public class RedactionTests
{
    private const string Secret = "TOPSECRET";

    /// <summary>
    /// A 300 × 200 pt page with two lines of Helvetica text: "Keep this line" above, "TOPSECRET value" below (one text
    /// object each). <paramref name="annots"/> are annotation dictionaries, <paramref name="extraObjects"/> come after them.
    /// </summary>
    internal static void TextPdf(string path, int rotate = 0, IReadOnlyList<string>? annots = null, string? acroForm = null)
    {
        const string content = "BT /F1 12 Tf 20 160 Td (Keep this line) Tj ET BT /F1 12 Tf 20 100 Td (" + Secret + " value) Tj ET";
        annots ??= [];
        var objects = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R" + (acroForm != null ? "/AcroForm 6 0 R" : "") + ">>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            $"<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 200]/Rotate {rotate}/Resources<</Font<</F1 5 0 R>>>>/Contents 4 0 R" +
            (annots.Count > 0 ? $"/Annots[{string.Join(" ", annots.Select((_, i) => $"{7 + i} 0 R"))}]" : "") + ">>",
            $"<</Length {content.Length}>>\nstream\n{content}\nendstream",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>",
            acroForm ?? "<<>>",
        };
        objects.AddRange(annots);
        Write(path, objects);
    }

    private static void Write(string path, IReadOnlyList<string> objects)
    {
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
        File.WriteAllText(path, sb.ToString(), Encoding.Latin1);
    }

    internal static async Task<EditableDocument> OpenAsync(string path)
    {
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    /// <summary>The file as text plus the decompressed data of all its Flate streams (where text or pictures would hide).</summary>
    private static string RawContent(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(bytes);
        var sb = new StringBuilder(text);
        foreach (Match m in Regex.Matches(text, @"<<((?:(?!>>stream).)*?)>>\s*stream\r?\n", RegexOptions.Singleline))
        {
            var dict = m.Groups[1].Value;
            if (!dict.Contains("/FlateDecode") || Regex.Match(dict, @"/Length (\d+)(?! \d+ R)") is not { Success: true } length)
                continue;
            try
            {
                using var z = new ZLibStream(new MemoryStream(bytes, m.Index + m.Length, int.Parse(length.Groups[1].Value)), CompressionMode.Decompress);
                using var plain = new MemoryStream();
                z.CopyTo(plain);
                sb.Append('\n').Append(Encoding.Latin1.GetString(plain.ToArray()));
            }
            catch (InvalidDataException)
            {
            }
        }
        return sb.ToString();
    }

    /// <summary>True if the text is in the file, literally or as hex string (as pdfium writes text).</summary>
    internal static bool FileContains(string path, string text)
    {
        var raw = RawContent(path);
        return raw.Contains(text) || raw.Contains(Convert.ToHexString(Encoding.Latin1.GetBytes(text)), StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<IReadOnlyList<OcrWord>> WordsAsync(string path, int page = 0)
    {
        await using var s = await PdfSource.OpenAsync(path);
        return await s.ReadWordsAsync(page);
    }

    internal static async Task<RectPt> BoxOfAsync(string path, string word)
    {
        var box = (await WordsAsync(path)).Single(w => w.Text == word).Box;
        return new RectPt(box.X - 1.5, box.Y - 1.5, box.Width + 3, box.Height + 3);
    }

    internal static async Task<RenderedPage> RenderAsync(string path, int scale = 2)
    {
        await using var s = await PdfSource.OpenAsync(path);
        await s.LoadPageSizesAsync();
        var size = s.GetPageSizeOrEstimate(0);
        return await s.RenderAsync(0, (int)size.Width * scale, (int)size.Height * scale);
    }

    /// <summary>Darkest and lightest grey value (per pixel the mean of the channels) inside a rect (page points at scale 2).</summary>
    internal static (int Min, int Max) Range(RenderedPage page, RectPt rect, int scale = 2)
    {
        int min = 255, max = 0;
        for (var y = (int)Math.Ceiling(rect.Y * scale) + 1; y < (int)(rect.Bottom * scale) - 1; y++)
            for (var x = (int)Math.Ceiling(rect.X * scale) + 1; x < (int)(rect.Right * scale) - 1; x++)
            {
                var i = y * page.Stride + x * 4;
                var v = (page.Pixels[i] + page.Pixels[i + 1] + page.Pixels[i + 2]) / 3;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        return (min, max);
    }

    /// <summary>Largest difference of any channel between two renders inside a rect (page points at scale 2).</summary>
    private static int Difference(RenderedPage a, RenderedPage b, RectPt rect, int scale = 2)
    {
        var max = 0;
        for (var y = (int)(rect.Y * scale); y < (int)(rect.Bottom * scale); y++)
            for (var x = (int)(rect.X * scale); x < (int)(rect.Right * scale); x++)
                for (var c = 0; c < 3; c++)
                    max = Math.Max(max, Math.Abs(a.Pixels[y * a.Stride + x * 4 + c] - b.Pixels[y * b.Stride + x * 4 + c]));
        return max;
    }

    internal static int Dark(RenderedPage page, RectPt rect, int scale = 2)
    {
        var count = 0;
        for (var y = (int)(rect.Y * scale); y < (int)(rect.Bottom * scale); y++)
            for (var x = (int)(rect.X * scale); x < (int)(rect.Right * scale); x++)
            {
                var i = y * page.Stride + x * 4;
                if (page.Pixels[i] + page.Pixels[i + 1] + page.Pixels[i + 2] < 300)
                    count++;
            }
        return count;
    }

    #region Editing

    [Fact]
    public async Task BoxesAreOneUndoStepAndCanBeMovedAndRemoved()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "a.pdf", 2);
        await using var d = await OpenAsync(path);
        var added = d.AddRedactions([(0, new RectPt(10, 10, 20, 5)), (1, new RectPt(5, 5, 10, 10))], RedactionFill.Black);
        Assert.Equal(2, added.Count);
        Assert.Equal(2, d.RedactedPageCount);
        Assert.True(d.IsDirty);

        d.Undo();
        Assert.Equal(0, d.RedactedPageCount);
        Assert.False(d.IsDirty);
        d.Redo();
        Assert.Single(d.Pages[1].Redactions);

        Assert.True(d.UpdateRedaction(added[0].Id, new RectPt(1, 2, 3, 4)));
        Assert.Equal(new RectPt(1, 2, 3, 4), d.Pages[0].Redactions[0].Rect);
        Assert.True(d.RemoveRedaction(added[1].Id));
        Assert.Empty(d.Pages[1].Redactions);
        Assert.False(d.RemoveRedaction(added[1].Id));

        // Boxes without area are not added (and no undo step for nothing)
        Assert.Empty(d.AddRedactions([(0, new RectPt(1, 1, 0, 5))], RedactionFill.White));
    }

    [Fact]
    public async Task CopiedTextLeavesOutWordsUnderABox()
    {
        using var dir = new TempDir();
        var path = dir.File("text.pdf");
        TextPdf(path);
        var box = await BoxOfAsync(path, Secret);
        await using var d = await OpenAsync(path);
        d.AddRedactions([(0, box)], RedactionFill.Black);

        var (text, _) = await PageText.ReadPagesAsync(d.Pages);
        Assert.DoesNotContain(Secret, text);
        Assert.Contains("Keep this line", text);
        Assert.Contains("value", text);
    }

    #endregion

    #region Saving

    [Fact]
    public async Task SavingRemovesTheBlackedOutTextFromTheFileAndKeepsTheRest()
    {
        using var dir = new TempDir();
        var path = dir.File("text.pdf");
        TextPdf(path);
        Assert.True(FileContains(path, Secret));
        var box = await BoxOfAsync(path, Secret);
        var keep = await BoxOfAsync(path, "Keep");
        var value = await BoxOfAsync(path, "value");
        var before = await RenderAsync(path);

        var saved = dir.File("saved.pdf");
        await using (var d = await OpenAsync(path))
        {
            d.AddRedactions([(0, box)], RedactionFill.Black);
            await d.SaveAsync(saved);
        }

        Assert.False(FileContains(saved, Secret), "The blacked-out text is still in the file");
        var words = (await WordsAsync(saved)).Select(w => w.Text).ToList();
        Assert.DoesNotContain(Secret, words);
        // The untouched line keeps its real text, the rest of the cut line comes back as (invisible) text
        Assert.Contains("Keep", words);
        Assert.Contains("line", words);
        Assert.Contains("value", words);

        var after = await RenderAsync(saved);
        Assert.Equal(0, Range(after, box).Max);                          // black box
        Assert.InRange(Difference(before, after, keep), 0, 0);             // untouched object, not even re-rendered
        var (darkBefore, darkAfter) = (Dark(before, value), Dark(after, value));
        Assert.InRange(darkAfter, darkBefore * 0.7, darkBefore * 1.3);     // still shown (as a picture now)
    }

    [Fact]
    public async Task CoveringInWhiteAlsoRemovesTheText()
    {
        using var dir = new TempDir();
        var path = dir.File("text.pdf");
        TextPdf(path);
        var box = await BoxOfAsync(path, Secret);
        var saved = dir.File("saved.pdf");
        await using (var d = await OpenAsync(path))
        {
            d.AddRedactions([(0, box)], RedactionFill.White);
            await d.SaveAsync(saved);
        }
        Assert.False(FileContains(saved, Secret));
        Assert.Equal(255, Range(await RenderAsync(saved), box).Min);
    }

    [Fact]
    public async Task OnARotatedPageTheBoxIsWhereItWasShown()
    {
        using var dir = new TempDir();
        var path = dir.File("text.pdf");
        TextPdf(path, rotate: 90);
        var box = await BoxOfAsync(path, Secret); // page space is after /Rotate, like the box
        var saved = dir.File("saved.pdf");
        await using (var d = await OpenAsync(path))
        {
            d.AddRedactions([(0, box)], RedactionFill.Black);
            d.Rotate([0], 90); // turned once more in the editor: the box turns with the page
            await d.SaveAsync(saved);
        }
        Assert.False(FileContains(saved, Secret));
        var words = await WordsAsync(saved);
        Assert.Contains(words, w => w.Text == "value");
        var shown = PageGeometry.Rotate(box, new PageSizePt(200, 300), 90);
        Assert.Equal(0, Range(await RenderAsync(saved), shown).Max);
    }

    private static byte[] NoiseJpeg(int w, int h)
    {
        using var bitmap = new SKBitmap(w, h);
        var random = new Random(7);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 255 / w), (byte)random.Next(120, 200), (byte)(y * 255 / h)));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    [Fact]
    public async Task APictureUnderABoxIsReplacedAndTheOriginalIsGoneFromTheFile()
    {
        using var dir = new TempDir();
        var path = dir.File("scan.pdf");
        var jpeg = NoiseJpeg(300, 200);
        TestPdf.WithJpeg(path, jpeg, 300, 200, gray: false, 300, 200);
        var box = new RectPt(100, 80, 60, 20);
        var before = await RenderAsync(path);
        var saved = dir.File("saved.pdf");
        await using (var d = await OpenAsync(path))
        {
            d.AddRedactions([(0, box)], RedactionFill.Black);
            await d.SaveAsync(saved);
        }

        var bytes = File.ReadAllBytes(saved);
        var probe = jpeg.AsSpan(jpeg.Length / 2, 64).ToArray();
        Assert.True(bytes.AsSpan().IndexOf(probe) < 0, "The original picture is still in the file");
        Assert.InRange(bytes.Length, 1, jpeg.Length * 8); // the new picture is a JPEG, not raw pixels

        var after = await RenderAsync(saved);
        Assert.Equal(0, Range(after, box).Max);
        Assert.InRange(Difference(before, after, new RectPt(10, 10, 60, 40)), 0, 40); // the rest of the picture looks the same
    }

    [Fact]
    public async Task AnnotationsAndFormValuesUnderABoxAreRemoved()
    {
        using var dir = new TempDir();
        var path = dir.File("annots.pdf");
        TextPdf(path,
            annots:
            [
                "<</Type/Annot/Subtype/Text/Rect[20 95 40 110]/Contents(SECRETNOTE)>>",
                "<</Type/Annot/Subtype/Text/Rect[250 170 270 190]/Contents(KEEPNOTE)>>",
                "<</Type/Annot/Subtype/Widget/FT/Tx/T(field)/Rect[20 97 90 109]/V(FIELDSECRET)>>",
            ],
            acroForm: "<</Fields[9 0 R]>>");
        var box = await BoxOfAsync(path, Secret);
        var saved = dir.File("saved.pdf");
        await using (var d = await OpenAsync(path))
        {
            d.AddRedactions([(0, box)], RedactionFill.Black);
            await d.SaveAsync(saved);
        }
        Assert.False(FileContains(saved, "SECRETNOTE"));
        Assert.False(FileContains(saved, "FIELDSECRET"));
        Assert.True(FileContains(saved, "KEEPNOTE"));
    }

    [Fact]
    public async Task RecognizedWordsUnderABoxAreNotWrittenAndExtractRedactsToo()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "scan.pdf", 1);
        var box = new RectPt(10, 10, 40, 20);
        await using var d = await OpenAsync(path);
        d.SetOcrWords(new Dictionary<int, IReadOnlyList<OcrWord>>
        {
            [d.Pages[0].Id] = [new OcrWord("HIDDENWORD", new RectPt(15, 12, 30, 12), 0.9f), new OcrWord("Shown", new RectPt(15, 100, 30, 12), 0.9f)],
        });
        d.AddRedactions([(0, box)], RedactionFill.Black);
        var extracted = dir.File("extract.pdf");
        await PdfSaver.ExtractAsync([d.Pages[0]], extracted);

        var words = (await WordsAsync(extracted)).Select(w => w.Text).ToList();
        Assert.Equal(["Shown"], words);
        Assert.False(FileContains(extracted, "HIDDENWORD"));
        Assert.Equal(0, Range(await RenderAsync(extracted), box).Max);
    }

    /// <summary>Imported pages consist of objects created while writing; pdfium would write those even when unused.</summary>
    [Fact]
    public async Task InsertedAndExtractedPagesLoseTheirTextToo()
    {
        using var dir = new TempDir();
        var text = dir.File("text.pdf");
        TextPdf(text);
        var box = await BoxOfAsync(text, Secret);
        var saved = dir.File("saved.pdf");
        var extracted = dir.File("extract.pdf");
        await using (var d = await OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 1)))
        {
            d.InsertFrom([await PdfSource.OpenAsync(text)], 1);
            d.AddRedactions([(1, box)], RedactionFill.Black);
            await PdfSaver.ExtractAsync([d.Pages[1]], extracted);
            await d.SaveAsync(saved);
        }
        foreach (var file in new[] { saved, extracted })
        {
            Assert.False(FileContains(file, Secret), file);
            Assert.True(FileContains(file, "Keep this line"), file);
        }
    }

    [Fact]
    public async Task OnTheDemoPdfTheWordsUnderTheBoxAreGoneAndTheRestStays()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "EasyPDF.slnx")))
            root = Path.GetDirectoryName(root);
        var demo = Path.Combine(root!, "demo-pdfs", "Annual Report 2025.pdf");
        using var dir = new TempDir();
        var original = dir.File("demo.pdf");
        File.Copy(demo, original);

        // The page with the most words; black out its longest word
        await using var source = await PdfSource.OpenAsync(original);
        var (page, words) = (0, (IReadOnlyList<OcrWord>)[]);
        for (var i = 0; i < Math.Min(10, source.PageCount); i++)
            if (await source.ReadWordsAsync(i) is { } w && w.Count > words.Count)
                (page, words) = (i, w);
        var target = words.OrderByDescending(w => w.Text.Length).First();
        var box = new RectPt(target.Box.X - 1, target.Box.Y - 1, target.Box.Width + 2, target.Box.Height + 2);
        var untouched = words.Where(w => !Redaction.Intersects(w.Box, new RectPt(box.X - 20, box.Y - 20, box.Width + 40, box.Height + 40)))
            .Select(w => w.Text).Distinct().ToList();

        var saved = dir.File("saved.pdf");
        await using (var d = await OpenAsync(original))
        {
            d.AddRedactions([(page, box)], RedactionFill.Black);
            await d.SaveAsync(saved);
        }
        await using var result = await PdfSource.OpenAsync(saved);
        var after = (await result.ReadWordsAsync(page)).ToList();
        Assert.DoesNotContain(after, w => Redaction.Intersects(w.Box, box));
        var afterTexts = after.Select(w => w.Text).ToHashSet();
        Assert.All(untouched, w => Assert.Contains(w, afterTexts));
        if (words.Count(w => w.Text == target.Text) == 1)
            Assert.DoesNotContain(target.Text, afterTexts);
        Assert.Equal(source.PageCount, result.PageCount);
    }

    [Fact]
    public async Task AStampUnderABoxIsBlackedOutToo()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "a.pdf", 1);
        var saved = dir.File("saved.pdf");
        await using (var d = await OpenAsync(path))
        {
            d.AddText(0, "STAMPTEXT", "Helvetica", 10, "#C00000", new RectPt(10, 10, 60, 14));
            d.AddRedactions([(0, new RectPt(5, 5, 80, 25))], RedactionFill.White);
            await d.SaveAsync(saved);
        }
        Assert.False(FileContains(saved, "STAMPTEXT"));
        Assert.Empty(await WordsAsync(saved));
    }

    #endregion
}
