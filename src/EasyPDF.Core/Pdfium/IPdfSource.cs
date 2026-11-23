using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EasyPDF.Core.Pdfium;

/// <summary>
/// An open PDF document as the user interface sees it: page count and sizes, rendered pages, the words of the text layer
/// and embedded files. <see cref="PdfSource"/> reads a local file with pdfium; the web version uses a source whose pdfium
/// runs on the server. Documents (<see cref="Documents.EditableDocument"/>) only hold pages of such sources; whatever
/// writes files goes through <see cref="PdfBackend"/>.
/// </summary>
public interface IPdfSource : IAsyncDisposable, IDisposable
{
    /// <summary>Full path of the file; in the web version a virtual path whose file name is the uploaded file's name.</summary>
    string Path { get; }

    string FileName { get; }

    int PageCount { get; }

    bool IsOpen { get; }

    /// <summary>Size of the first page, used as estimate for pages whose size is not known yet.</summary>
    PageSizePt EstimatedPageSize { get; }

    /// <summary>The page size if it has been read already (see <see cref="LoadPageSizesAsync"/>).</summary>
    bool TryGetPageSize(int index, out PageSizePt size);

    PageSizePt GetPageSizeOrEstimate(int index);

    /// <summary>The exact size of one page.</summary>
    Task<PageSizePt> GetPageSizeAsync(int index, CancellationToken token = default);

    /// <summary>Reads all page sizes; <paramref name="progress"/> receives the number of sizes known so far.</summary>
    Task LoadPageSizesAsync(IProgress<int>? progress = null, CancellationToken token = default);

    /// <summary>Number of characters in the page's text layer (0 = the page has no text, e.g. a scan).</summary>
    Task<int> CountTextCharsAsync(int pageIndex, CancellationToken token = default);

    /// <summary>The words of the page's text layer, boxes in the page's own space (see <see cref="Ocr.OcrWord"/>).</summary>
    Task<IReadOnlyList<Ocr.OcrWord>> ReadWordsAsync(int pageIndex, CancellationToken token = default);

    /// <summary>The font most of the page's visible text is written in, with its size on the page; null if the page has no visible text.</summary>
    Task<Fonts.PageFont?> ReadMainFontAsync(int pageIndex, CancellationToken token = default);

    /// <summary>Renders a page to <paramref name="width"/> × <paramref name="height"/> pixels, additionally rotated by 0/90/180/270° clockwise.</summary>
    Task<RenderedPage> RenderAsync(int pageIndex, int width, int height, int extraRotation = 0,
        int priority = PdfiumWorker.PriorityVisible, CancellationToken token = default);

    /// <summary>
    /// Renders only a part of a page: the page as if rendered to <paramref name="pageWidth"/> × <paramref name="pageHeight"/>
    /// pixels (turned by <paramref name="extraRotation"/>), of which the <paramref name="width"/> × <paramref name="height"/>
    /// pixels from (<paramref name="x"/>, <paramref name="y"/>) on.
    /// </summary>
    Task<RenderedPage> RenderAreaAsync(int pageIndex, int pageWidth, int pageHeight, int extraRotation, int x, int y, int width, int height,
        int priority = PdfiumWorker.PriorityVisible, CancellationToken token = default);

    /// <summary>The files embedded in the PDF (empty if it has none).</summary>
    Task<IReadOnlyList<PdfAttachment>> ListAttachmentsAsync(CancellationToken token = default);

    /// <summary>The content of an embedded file (<see cref="PdfAttachment.Index"/>).</summary>
    Task<byte[]> ReadAttachmentAsync(int index, CancellationToken token = default);

    /// <summary>For helper files made by the program itself (blank pages): the file is deleted when the source is disposed.</summary>
    bool DeleteFileOnDispose { get; set; }
}
