using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EasyPDF.App.Services;

namespace EasyPDF.App.Tests;

/// <summary>Scripted dialog answers; records what the view model asked for.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public string? PickedFile = null;
    public List<string> PickedFiles = [];
    public string? SavePath;
    public RotateResult? RotateAnswer = null;
    public UnsavedChoice UnsavedAnswer = UnsavedChoice.Cancel;
    public readonly List<string> Errors = [];
    public RotateDialogContext? LastRotateContext;
    public int UnsavedAsked;
    public bool Closed;

    public Task<string?> PickPdfFileAsync() => Task.FromResult(PickedFile);

    public Task<IReadOnlyList<string>> PickPdfFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([
        .. PickedFiles
    ]);

    public string? SuggestedFileName;

    public Task<string?> PickSavePathAsync(string suggestedFileName, string? initialFolder)
    {
        SuggestedFileName = suggestedFileName;
        return Task.FromResult(SavePath);
    }

    public string? SaveFilePath;
    public string? SuggestedSaveFileName;

    public Task<string?> PickSaveFilePathAsync(string suggestedFileName, string? initialFolder)
    {
        SuggestedSaveFileName = suggestedFileName;
        return Task.FromResult(SaveFilePath);
    }

    /// <summary>Files handed to the system's program, with their content at that moment.</summary>
    public readonly List<(string Path, byte[] Content)> OpenedFiles = [];
    public bool OpenResult = true;

    public Task<bool> OpenInSystemAsync(string path)
    {
        OpenedFiles.Add((path, File.ReadAllBytes(path)));
        return Task.FromResult(OpenResult);
    }

    /// <summary>Picks from the listed attachments (null = closed without a choice).</summary>
    public Func<IReadOnlyList<EasyPDF.Core.Pdfium.PdfAttachment>, AttachmentChoice?>? AttachmentAnswer;

    public Task<AttachmentChoice?> ShowAttachmentsAsync(IReadOnlyList<EasyPDF.Core.Pdfium.PdfAttachment> attachments) =>
        Task.FromResult(AttachmentAnswer?.Invoke(attachments));

    public Task<RotateResult?> ShowRotateDialogAsync(RotateDialogContext context)
    {
        LastRotateContext = context;
        return Task.FromResult(RotateAnswer);
    }

    public SplitRequest? SplitAnswer;
    public SplitDialogContext? LastSplitContext;
    public ConcatenateResult? ConcatAnswer;
    public ConcatenateContext? LastConcatContext;

    /// <summary>Pages answered for the insert-range dialog; null = all pages of the file.</summary>
    public IReadOnlyList<int>? InsertRangeAnswer;
    public bool InsertRangeCancel;
    public InsertRangeContext? LastInsertRangeContext;

    public Task<IReadOnlyList<int>?> ShowInsertRangeDialogAsync(InsertRangeContext context)
    {
        LastInsertRangeContext = context;
        if (InsertRangeCancel)
            return Task.FromResult<IReadOnlyList<int>?>(null);
        return Task.FromResult<IReadOnlyList<int>?>(InsertRangeAnswer ?? [.. Enumerable.Range(0, context.PageCount)]);
    }

    /// <summary>Builds the answer of the replace dialog from its context (null = cancelled).</summary>
    public Func<ReplacePagesContext, Task<ReplacePagesResult?>>? ReplaceAnswer;
    public ReplacePagesContext? LastReplaceContext;

    public Task<ReplacePagesResult?> ShowReplacePagesDialogAsync(ReplacePagesContext context)
    {
        LastReplaceContext = context;
        return ReplaceAnswer?.Invoke(context) ?? Task.FromResult<ReplacePagesResult?>(null);
    }

    public PrintRequest? PrintAnswer;
    public PrintDialogContext? LastPrintContext;

    public Task<PrintRequest?> ShowPrintDialogAsync(PrintDialogContext context)
    {
        LastPrintContext = context;
        return Task.FromResult(PrintAnswer);
    }

    public OcrRequest? OcrAnswer;
    public OcrDialogContext? LastOcrContext;

    public Task<OcrRequest?> ShowOcrDialogAsync(OcrDialogContext context)
    {
        LastOcrContext = context;
        return Task.FromResult(OcrAnswer);
    }

    public Task<SplitRequest?> ShowSplitDialogAsync(SplitDialogContext context)
    {
        LastSplitContext = context;
        return Task.FromResult(SplitAnswer);
    }

    public Task<ConcatenateResult?> ShowConcatenateDialogAsync(ConcatenateContext context)
    {
        LastConcatContext = context;
        return Task.FromResult(ConcatAnswer);
    }

    public Task<UnsavedChoice> AskUnsavedChangesAsync(string fileName)
    {
        UnsavedAsked++;
        return Task.FromResult(UnsavedAnswer);
    }

    public Task ShowAboutAsync() => Task.CompletedTask;

    public int IntroVideoShown;

    public Task ShowIntroVideoAsync()
    {
        IntroVideoShown++;
        return Task.CompletedTask;
    }

    public int QuickGuideShown;

    public Task ShowQuickGuideAsync()
    {
        QuickGuideShown++;
        return Task.CompletedTask;
    }

    public Task ShowSettingsAsync() => Task.CompletedTask;

    public Task ShowErrorAsync(string message, string? details = null)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }

    public readonly List<string> Infos = [];

    public Task ShowInfoAsync(string message, string? details = null)
    {
        Infos.Add(message);
        return Task.CompletedTask;
    }

    /// <summary>Answer of the reduce-size dialog (null = cancelled) and what it was asked with.</summary>
    public EasyPDF.Core.Documents.ImageReduction? ReduceAnswer;
    public EasyPDF.Core.Documents.ImageReduction? LastReducePreset;

    public Task<EasyPDF.Core.Documents.ImageReduction?> ShowReduceSizeDialogAsync(EasyPDF.Core.Documents.ImageReduction last)
    {
        LastReducePreset = last;
        return Task.FromResult(ReduceAnswer);
    }

    /// <summary>The text put on the clipboard last; null = nothing copied.</summary>
    public string? ClipboardText;

    public Task CopyToClipboardAsync(string text)
    {
        ClipboardText = text;
        return Task.CompletedTask;
    }

    public void CloseWindow() => Closed = true;

    /// <summary>Answer of the warning before overwriting blacked-out content, and how often it was shown (with what).</summary>
    public RedactedSaveAnswer RedactedAnswer = new(RedactedSaveChoice.Cancel, false);
    public int RedactedAsked;
    public (string FileName, int PageCount)? LastRedactedQuestion;

    public Task<RedactedSaveAnswer> AskOverwriteRedactedAsync(string fileName, int pageCount)
    {
        RedactedAsked++;
        LastRedactedQuestion = (fileName, pageCount);
        return Task.FromResult(RedactedAnswer);
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "easypdf-apptests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Writes a PDF with pages of width 100 + widthBase + 10·i (the width identifies the page).</summary>
    public string Pdf(string name, int pages, int widthBase = 0, params (string Title, int Page)[] outline)
    {
        var objects = new List<string>
        {
            outline.Length > 0 ? "<</Type/Catalog/Pages 2 0 R/Outlines 3 0 R>>" : "<</Type/Catalog/Pages 2 0 R>>",
            $"<</Type/Pages/Kids[{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{4 + i} 0 R"))}]/Count {pages}>>",
            outline.Length > 0 ? $"<</Type/Outlines/First {4 + pages} 0 R/Last {4 + pages + outline.Length - 1} 0 R/Count {outline.Length}>>" : "<<>>",
        };
        for (var i = 0; i < pages; i++)
            objects.Add($"<</Type/Page/Parent 2 0 R/MediaBox[0 0 {100 + widthBase + 10 * i} 200]>>");
        for (var i = 0; i < outline.Length; i++)
        {
            var prev = i > 0 ? $"/Prev {4 + pages + i - 1} 0 R" : "";
            var next = i < outline.Length - 1 ? $"/Next {4 + pages + i + 1} 0 R" : "";
            objects.Add($"<</Title({outline[i].Title})/Parent 3 0 R{prev}{next}/Dest[{4 + outline[i].Page} 0 R/Fit]>>");
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
        var path = File(name);
        System.IO.File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }

    /// <summary>One 200 x 300 pt page with the given files embedded (document-level attachments, /EmbeddedFiles name tree).</summary>
    public string AttachmentPdf(string name, params (string Name, byte[] Data)[] files)
    {
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
        var path = File(name);
        System.IO.File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    /// <summary>A white 200 x 300 pt page with a pure blue block in its bottom-right corner (the outer 20 % in both directions).</summary>
    public string CornerPdf(string name)
    {
        const string content = "0 0 1 rg 160 0 40 60 re f";
        var objects = new[]
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 300]/Contents 4 0 R>>",
            $"<</Length {content.Length}>>stream\n{content}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<</Size {objects.Length + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        var path = File(name);
        System.IO.File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }

    /// <summary>A one-page "scan": a grey 1200 x 1200 JPEG with fine noise on a 4 x 4 inch page (300 dpi).</summary>
    public string ScanPdf(string name)
    {
        var random = new Random(1);
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1200, 1200, SkiaSharp.SKColorType.Gray8, SkiaSharp.SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)Math.Clamp(i % bitmap.RowBytes / 5 + random.Next(-12, 13), 0, 255);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        var jpeg = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 92).ToArray();

        var content = "q 288 0 0 288 0 0 cm /Im1 Do Q";
        using var file = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) => file.Write(Encoding.ASCII.GetBytes(s));
        void Object(string body, byte[]? data = null)
        {
            offsets.Add(file.Position);
            Write($"{offsets.Count} 0 obj\n{body}");
            if (data != null)
            {
                Write("\nstream\n");
                file.Write(data);
                Write("\nendstream");
            }
            Write("\nendobj\n");
        }
        Write("%PDF-1.4\n");
        Object("<</Type/Catalog/Pages 2 0 R>>");
        Object("<</Type/Pages/Kids[3 0 R]/Count 1>>");
        Object("<</Type/Page/Parent 2 0 R/MediaBox[0 0 288 288]/Resources<</XObject<</Im1 4 0 R>>>>/Contents 5 0 R>>");
        Object($"<</Type/XObject/Subtype/Image/Width 1200/Height 1200/ColorSpace/DeviceGray/BitsPerComponent 8/Filter/DCTDecode/Length {jpeg.Length}>>", jpeg);
        Object($"<</Length {content.Length}>>", Encoding.ASCII.GetBytes(content));
        var xref = file.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<</Size {offsets.Count + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        var path = File(name);
        System.IO.File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

internal static class RepoPaths
{
    public static string Root
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(System.IO.Path.Combine(dir, "EasyPDF.slnx")))
                dir = System.IO.Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    /// <summary>The brand profile the build uses: <c>Brand</c> (Directory.Build.props), passed in as assembly metadata.</summary>
    public static string Brand { get; } = typeof(RepoPaths).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).Cast<System.Reflection.AssemblyMetadataAttribute>()
        .Single(a => a.Key == "Brand").Value!;

    /// <summary>branding/&lt;brand&gt;/ of the brand the build uses.</summary>
    public static string BrandDir => System.IO.Path.Combine(Root, "branding", Brand);

    /// <summary>A value of the brand profile (branding/&lt;brand&gt;/Branding.props), "" if it has none.</summary>
    public static string BrandProp(string name) =>
        System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(BrandDir, "Branding.props")).Descendants(name).SingleOrDefault()?.Value.Trim() ?? "";
}
