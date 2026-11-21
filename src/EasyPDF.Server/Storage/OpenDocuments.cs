using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Pdfium;
using EasyPDF.Remote;

namespace EasyPDF.Server.Storage;

/// <summary>
/// The stored files opened with pdfium, by file id: opened on first use and kept open while they are used (a page view
/// renders many pages one after the other). At most <see cref="MaxOpen"/> stay open; the least recently used is closed.
/// </summary>
public sealed class OpenDocuments : IAsyncDisposable
{
    public const int MaxOpen = 64;

    private readonly ConcurrentDictionary<string, Lazy<Task<PdfSource>>> _open = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _used = new(StringComparer.Ordinal);
    private long _clock;

    public async Task<PdfSource> GetAsync(StoredFile file, CancellationToken token)
    {
        _used[file.Id] = Interlocked.Increment(ref _clock);
        var lazy = _open.GetOrAdd(file.Id, _ => new Lazy<Task<PdfSource>>(() => OpenAsync(file.Path)));
        try
        {
            var source = await lazy.Value.WaitAsync(token);
            await TrimAsync(file.Id);
            return source;
        }
        catch (PdfOpenException)
        {
            _open.TryRemove(file.Id, out _);
            throw;
        }
    }

    private static async Task<PdfSource> OpenAsync(string path)
    {
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return source;
    }

    /// <summary>The file's description for the browser (opens it, so a damaged file is found right away).</summary>
    public async Task<RemoteFileInfo> DescribeAsync(StoredFile file, CancellationToken token)
    {
        var source = await GetAsync(file, token);
        var sizes = Enumerable.Range(0, source.PageCount).Select(source.GetPageSizeOrEstimate).ToArray();
        return new RemoteFileInfo(file.Id, file.Name, source.PageCount, sizes, file.Size);
    }

    public async Task CloseAsync(string id)
    {
        _used.TryRemove(id, out _);
        if (_open.TryRemove(id, out var lazy) && lazy.IsValueCreated)
        {
            try
            {
                await (await lazy.Value).DisposeAsync();
            }
            catch (PdfOpenException)
            {
            }
        }
    }

    private async Task TrimAsync(string keep)
    {
        while (_open.Count > MaxOpen)
        {
            var oldest = _used.Where(p => p.Key != keep).OrderBy(p => p.Value).Select(p => p.Key).FirstOrDefault();
            if (oldest == null)
                return;
            await CloseAsync(oldest);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _open.Keys.ToArray())
            await CloseAsync(id);
    }
}
