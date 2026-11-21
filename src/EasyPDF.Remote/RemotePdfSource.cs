using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Remote;

/// <summary>
/// A PDF that lives on the server: every page size is known from the upload, pages are rendered by the server's pdfium
/// and come back as PNG. <see cref="Path"/> is a virtual path (<see cref="RemoteFiles.PathOf"/>) ending in the file name.
/// </summary>
public sealed class RemotePdfSource : IPdfSource, IServerOcr
{
    private readonly RemoteFiles _files;
    private int _disposed;

    internal RemotePdfSource(RemoteFiles files, RemoteFileInfo info)
    {
        _files = files;
        Info = info;
        Path = RemoteFiles.PathOf(info);
    }

    public RemoteFileInfo Info { get; }

    public string Id => Info.Id;

    public string Path { get; }

    public string FileName => Info.Name;

    public int PageCount => Info.PageCount;

    public bool IsOpen => _disposed == 0;

    public PageSizePt EstimatedPageSize => Info.PageSizes.Count > 0 ? Info.PageSizes[0] : new PageSizePt(595, 842);

    public bool DeleteFileOnDispose { get; set; }

    public bool TryGetPageSize(int index, out PageSizePt size)
    {
        if (index >= 0 && index < Info.PageSizes.Count)
        {
            size = Info.PageSizes[index];
            return true;
        }
        size = EstimatedPageSize;
        return false;
    }

    public PageSizePt GetPageSizeOrEstimate(int index) => TryGetPageSize(index, out var s) ? s : EstimatedPageSize;

    public Task<PageSizePt> GetPageSizeAsync(int index, CancellationToken token = default) => Task.FromResult(GetPageSizeOrEstimate(index));

    public Task LoadPageSizesAsync(IProgress<int>? progress = null, CancellationToken token = default)
    {
        progress?.Report(PageCount);
        return Task.CompletedTask;
    }

    public Task<int> CountTextCharsAsync(int pageIndex, CancellationToken token = default) =>
        _files.Api.CountTextCharsAsync(Id, pageIndex, token);

    public Task<IReadOnlyList<OcrWord>> ReadWordsAsync(int pageIndex, CancellationToken token = default) =>
        _files.Api.ReadWordsAsync(Id, pageIndex, token);

    public Task<RenderedPage> RenderAsync(int pageIndex, int width, int height, int extraRotation = 0,
        int priority = PdfiumWorker.PriorityVisible, CancellationToken token = default) =>
        _files.Api.RenderAsync(Id, pageIndex, Math.Max(1, width), Math.Max(1, height), extraRotation, token);

    public Task<RenderedPage> RenderAreaAsync(int pageIndex, int pageWidth, int pageHeight, int extraRotation, int x, int y, int width, int height,
        int priority = PdfiumWorker.PriorityVisible, CancellationToken token = default) =>
        _files.Api.RenderAreaAsync(Id, pageIndex, pageWidth, pageHeight, extraRotation, x, y, width, height, token);

    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(int pageIndex, int extraRotation, CancellationToken token = default) =>
        _files.Api.RecognizeAsync(Id, pageIndex, extraRotation, token);

    public Task<IReadOnlyList<PdfAttachment>> ListAttachmentsAsync(CancellationToken token = default) =>
        _files.Api.ListAttachmentsAsync(Id, token);

    public Task<byte[]> ReadAttachmentAsync(int index, CancellationToken token = default) =>
        _files.Api.ReadAttachmentAsync(Id, index, token);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _files.Release(Info);
    }
}
