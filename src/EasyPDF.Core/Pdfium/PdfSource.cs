using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PDFiumCore;

namespace EasyPDF.Core.Pdfium;

/// <summary>
/// An open PDF file. pdfium reads it block-wise through a callback (<c>FPDF_LoadCustomDocument</c>),
/// the file is never loaded completely into memory. All pdfium access goes through <see cref="PdfiumWorker"/>.
/// </summary>
public sealed class PdfSource : IPdfSource
{
    private const int FpdfAnnot = 0x01;
    private const int SizeBatch = 256;

    private readonly PdfiumWorker _worker;
    private FileStream? _stream;
    private FPDF_FILEACCESS? _access; // must stay alive as long as the document is open
    private PDFiumCore.Delegates.Func_int___IntPtr_ulong_bytePtr_ulong? _getBlock; // the binding does not keep the delegate alive itself
    private FpdfDocumentT? _doc;
    private PageSizePt?[] _sizes = [];
    private PageSizePt _estimate = new(595, 842);

    private PdfSource(string path, PdfiumWorker worker)
    {
        Path = path;
        _worker = worker;
    }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public int PageCount { get; private set; }

    public bool IsOpen => _doc != null;

    internal PdfiumWorker Worker => _worker;

    /// <summary>The local pdfium source behind <paramref name="source"/> (writing files needs one).</summary>
    internal static PdfSource Local(IPdfSource source) => source as PdfSource
        ?? throw new NotSupportedException($"{source.GetType().Name} is not a local pdfium source.");

    /// <summary>pdfium document handle; only use on the worker thread.</summary>
    internal FpdfDocumentT Handle => _doc ?? throw new ObjectDisposedException(nameof(PdfSource));

    public static async Task<PdfSource> OpenAsync(string path, PdfiumWorker? worker = null, CancellationToken token = default)
    {
        worker ??= PdfiumWorker.Shared;
        var source = new PdfSource(System.IO.Path.GetFullPath(path), worker);
        await source.OpenCoreAsync(token).ConfigureAwait(false);
        return source;
    }

    private unsafe Task OpenCoreAsync(CancellationToken token) => _worker.Run(() =>
    {
        FileStream stream;
        try
        {
            stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfOpenException(Path, 2);
        }

        PDFiumCore.Delegates.Func_int___IntPtr_ulong_bytePtr_ulong getBlock =
            (param, position, buffer, size) => ReadBlock(stream, position, buffer, size);
        var access = new FPDF_FILEACCESS { MFileLen = (ulong)stream.Length, MGetBlock = getBlock };
        var doc = fpdfview.FPDF_LoadCustomDocument(access, null);
        if (doc == null)
        {
            var error = fpdfview.FPDF_GetLastError();
            stream.Dispose();
            throw new PdfOpenException(Path, error);
        }

        _stream = stream;
        _access = access;
        _getBlock = getBlock;
        _doc = doc;
        PageCount = fpdfview.FPDF_GetPageCount(doc);
        if (_sizes.Length != PageCount)
            _sizes = new PageSizePt?[PageCount];
        if (PageCount > 0)
        {
            var first = ReadSize(0);
            _sizes[0] = first;
            _estimate = first;
        }
    }, PdfiumWorker.PriorityVisible, token);

    private static unsafe int ReadBlock(FileStream stream, ulong position, byte* buffer, ulong size)
    {
        try
        {
            stream.Position = (long)position;
            var span = new Span<byte>(buffer, (int)size);
            var total = 0;
            while (total < span.Length)
            {
                var n = stream.Read(span.Slice(total));
                if (n <= 0)
                    return 0;
                total += n;
            }
            return 1;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private PageSizePt ReadSize(int index)
    {
        var size = new FS_SIZEF_();
        return fpdfview.FPDF_GetPageSizeByIndexF(Handle, index, size) != 0
            ? new PageSizePt(size.Width, size.Height)
            : _estimate;
    }

    /// <summary>Size of the first page, used as estimate for pages whose size is not known yet.</summary>
    public PageSizePt EstimatedPageSize => _estimate;

    /// <summary>The page size if it has been read already (see <see cref="LoadPageSizesAsync"/>).</summary>
    public bool TryGetPageSize(int index, out PageSizePt size)
    {
        var sizes = _sizes;
        if (index >= 0 && index < sizes.Length && sizes[index] is { } s)
        {
            size = s;
            return true;
        }
        size = _estimate;
        return false;
    }

    public PageSizePt GetPageSizeOrEstimate(int index) => TryGetPageSize(index, out var s) ? s : _estimate;

    /// <summary>The exact size of one page (read on the worker if it is not known yet).</summary>
    public Task<PageSizePt> GetPageSizeAsync(int index, CancellationToken token = default) =>
        _worker.Run(() => _sizes[index] ??= ReadSize(index), PdfiumWorker.PriorityBackground, token);

    /// <summary>Number of characters in the page's text layer (0 = the page has no text, e.g. a scan).</summary>
    public Task<int> CountTextCharsAsync(int pageIndex, CancellationToken token = default) =>
        _worker.Run(() => PageTextReader.CountChars(Handle, pageIndex), PdfiumWorker.PriorityBackground, token);

    /// <summary>The words of the page's text layer, boxes in the page's own space (see <see cref="Ocr.OcrWord"/>).</summary>
    public Task<IReadOnlyList<Ocr.OcrWord>> ReadWordsAsync(int pageIndex, CancellationToken token = default) =>
        _worker.Run<IReadOnlyList<Ocr.OcrWord>>(() => PageTextReader.ReadWords(Handle, pageIndex), PdfiumWorker.PriorityBackground, token);

    /// <summary>The font most of the page's visible text is written in (see <see cref="PageTextReader.ReadMainFont"/>).</summary>
    public Task<Fonts.PageFont?> ReadMainFontAsync(int pageIndex, CancellationToken token = default) =>
        _worker.Run(() => PageTextReader.ReadMainFont(Handle, pageIndex), PdfiumWorker.PriorityVisible, token);

    /// <summary>
    /// Reads all page sizes in batches on the worker (other jobs can interleave).
    /// <paramref name="progress"/> receives the number of sizes known so far.
    /// </summary>
    public async Task LoadPageSizesAsync(IProgress<int>? progress = null, CancellationToken token = default)
    {
        for (var start = 0; start < PageCount; start += SizeBatch)
        {
            var from = start;
            var done = await _worker.Run(() =>
            {
                if (_doc == null)
                    return -1;
                var end = Math.Min(PageCount, from + SizeBatch);
                for (var i = from; i < end; i++)
                    _sizes[i] ??= ReadSize(i);
                return end;
            }, PdfiumWorker.PriorityBackground, token).ConfigureAwait(false);
            if (done < 0)
                return;
            progress?.Report(done);
        }
    }

    /// <summary>Renders a page to <paramref name="width"/> × <paramref name="height"/> pixels, additionally rotated by 0/90/180/270° clockwise.</summary>
    public Task<RenderedPage> RenderAsync(int pageIndex, int width, int height, int extraRotation = 0,
        int priority = PdfiumWorker.PriorityVisible, CancellationToken token = default) =>
        _worker.Run(() => RenderCore(pageIndex, width, height, extraRotation), priority, token);

    /// <summary>
    /// Renders only a part of a page: the page as if rendered to <paramref name="pageWidth"/> × <paramref name="pageHeight"/>
    /// pixels (turned by <paramref name="extraRotation"/>), of which the <paramref name="width"/> × <paramref name="height"/>
    /// pixels from (<paramref name="x"/>, <paramref name="y"/>) on.
    /// </summary>
    public Task<RenderedPage> RenderAreaAsync(int pageIndex, int pageWidth, int pageHeight, int extraRotation, int x, int y, int width, int height,
        int priority = PdfiumWorker.PriorityVisible, CancellationToken token = default) =>
        _worker.Run(() => RenderCore(pageIndex, width, height, extraRotation, pageWidth, pageHeight, x, y), priority, token);

    private RenderedPage RenderCore(int pageIndex, int width, int height, int extraRotation) =>
        RenderCore(pageIndex, width, height, extraRotation, width, height, 0, 0);

    private RenderedPage RenderCore(int pageIndex, int width, int height, int extraRotation, int pageWidth, int pageHeight, int x, int y)
    {
        if (_doc == null)
            throw new ObjectDisposedException(nameof(PdfSource));
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var page = fpdfview.FPDF_LoadPage(_doc, pageIndex);
        if (page == null)
            throw new InvalidOperationException($"Page {pageIndex + 1} could not be loaded.");
        FpdfBitmapT? bitmap = null;
        try
        {
            bitmap = fpdfview.FPDFBitmapCreate(width, height, 1);
            if (bitmap == null)
                throw new OutOfMemoryException("Bitmap allocation failed.");
            fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            var rotate = ((extraRotation / 90) % 4 + 4) % 4;
            fpdfview.FPDF_RenderPageBitmap(bitmap, page, -x, -y, Math.Max(1, pageWidth), Math.Max(1, pageHeight), rotate, FpdfAnnot);

            var stride = fpdfview.FPDFBitmapGetStride(bitmap);
            var pixels = new byte[(long)stride * height];
            Marshal.Copy(fpdfview.FPDFBitmapGetBuffer(bitmap), pixels, 0, pixels.Length);
            return new RenderedPage(width, height, stride, pixels);
        }
        finally
        {
            if (bitmap != null)
                fpdfview.FPDFBitmapDestroy(bitmap);
            fpdfview.FPDF_ClosePage(page);
        }
    }

    /// <summary>Reads the bookmark tree (empty if the file has none).</summary>
    public Task<IReadOnlyList<OutlineNode>> ReadOutlineAsync(CancellationToken token = default) =>
        _worker.Run<IReadOnlyList<OutlineNode>>(() => OutlineReader.Read(Handle), PdfiumWorker.PriorityBackground, token);

    /// <summary>The files embedded in the PDF (empty if it has none).</summary>
    public Task<IReadOnlyList<PdfAttachment>> ListAttachmentsAsync(CancellationToken token = default) =>
        _worker.Run<IReadOnlyList<PdfAttachment>>(() => PdfAttachments.List(Handle), PdfiumWorker.PriorityBackground, token);

    /// <summary>The content of an embedded file (<see cref="PdfAttachment.Index"/>).</summary>
    public Task<byte[]> ReadAttachmentAsync(int index, CancellationToken token = default) =>
        _worker.Run(() => PdfAttachments.Read(Handle, index), PdfiumWorker.PriorityVisible, token);

    /// <summary>Releases the file handle but keeps the path, so the source can be reopened with <see cref="ReopenAsync"/>.</summary>
    public Task CloseAsync() => _worker.Run(CloseCore, PdfiumWorker.PriorityVisible);

    private void CloseCore()
    {
        if (_doc != null)
        {
            fpdfview.FPDF_CloseDocument(_doc);
            _doc = null;
        }
        _stream?.Dispose();
        _stream = null;
        _access = null;
        _getBlock = null;
    }

    public Task ReopenAsync() => IsOpen ? Task.CompletedTask : OpenCoreAsync(CancellationToken.None);

    /// <summary>For helper files made by the program itself (blank pages): the file is deleted when the source is disposed.</summary>
    public bool DeleteFileOnDispose { get; set; }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        if (DeleteFileOnDispose)
        {
            try { File.Delete(Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => _ = CloseAsync();
}

internal static class OutlineReader
{
    private const int MaxDepth = 32;
    private const int MaxNodes = 100_000;

    public static List<OutlineNode> Read(FpdfDocumentT doc)
    {
        var count = 0;
        return ReadChildren(doc, null, 0, ref count);
    }

    private static List<OutlineNode> ReadChildren(FpdfDocumentT doc, FpdfBookmarkT? parent, int depth, ref int count)
    {
        var list = new List<OutlineNode>();
        if (depth >= MaxDepth)
            return list;
        for (var b = fpdf_doc.FPDFBookmarkGetFirstChild(doc, parent);
             b != null && count < MaxNodes;
             b = fpdf_doc.FPDFBookmarkGetNextSibling(doc, b))
        {
            count++;
            var node = new OutlineNode { Title = Title(b), SourcePageIndex = PageIndex(doc, b) };
            node.Children.AddRange(ReadChildren(doc, b, depth + 1, ref count));
            list.Add(node);
        }
        return list;
    }

    private static int PageIndex(FpdfDocumentT doc, FpdfBookmarkT bookmark)
    {
        var dest = fpdf_doc.FPDFBookmarkGetDest(doc, bookmark);
        if (dest == null)
        {
            var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
            if (action != null && fpdf_doc.FPDFActionGetType(action) == 1) // PDFACTION_GOTO
                dest = fpdf_doc.FPDFActionGetDest(doc, action);
        }
        return dest == null ? -1 : fpdf_doc.FPDFDestGetDestPageIndex(doc, dest);
    }

    private static unsafe string Title(FpdfBookmarkT bookmark)
    {
        var length = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0); // bytes incl. terminator
        if (length <= 2)
            return string.Empty;
        var memory = Marshal.AllocHGlobal((int)length);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(bookmark, memory, length);
            return Encoding.Unicode.GetString((byte*)memory, (int)length - 2);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }
}
