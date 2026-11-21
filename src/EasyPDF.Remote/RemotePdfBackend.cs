using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Remote;

/// <summary>
/// Where finished files go in the browser: the targets the user chose in the save dialog, by the (virtual) path the
/// dialog returned. Implemented by the browser program.
/// </summary>
public interface ISaveTargets
{
    /// <summary>Whether <paramref name="path"/> is a target the user chose (so "Save" can write to it again).</summary>
    bool Has(string path);

    /// <summary>Writes the file to the target the user chose for <paramref name="path"/> (or offers it as download).</summary>
    Task WriteAsync(string path, byte[] content, CancellationToken token);
}

/// <summary>The web version's files: everything is opened, made and written by the server.</summary>
public sealed class RemotePdfBackend(RemoteFiles files, ISaveTargets targets) : IPdfBackend
{
    public RemoteFiles Files { get; } = files;

    public bool Exists(string path) => Files.TryGet(path, out _);

    public bool CanOverwrite(string path) => targets.Has(path);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _written = new(StringComparer.Ordinal);

    public long? FileSize(string path) =>
        _written.TryGetValue(path, out var size) ? size : Files.TryGet(path, out var info) && info.Size > 0 ? info.Size : null;

    public Task<IPdfSource> OpenAsync(string path, CancellationToken token = default)
    {
        if (!Files.TryGet(path, out var info))
            throw new PdfOpenException(path, 2);
        return Task.FromResult<IPdfSource>(Files.Open(info));
    }

    public async Task<IPdfSource> CreateBlankAsync(PageSizePt size, CancellationToken token = default)
    {
        var info = await Files.Api.CreateBlankAsync(size, token).ConfigureAwait(false);
        return Files.Open(info);
    }

    public async Task<(IPdfSource Saved, int ReducedImages)> SaveAsync(IReadOnlyList<PageEntry> pages, IPdfSource baseSource, string path,
        IReadOnlyList<IPdfSource> openSources, IProgress<double>? progress = null, CancellationToken token = default,
        ImageReduction? reduceImages = null)
    {
        progress?.Report(0.1);
        var name = Path.GetFileName(path);
        var info = await Files.Api.BuildAsync(new BuildRequest(IdOf(baseSource), name, ToRemote(pages), reduceImages), token).ConfigureAwait(false);
        progress?.Report(0.6);
        var content = await Files.Api.DownloadAsync(info.Id, token).ConfigureAwait(false);
        progress?.Report(0.9);
        await targets.WriteAsync(path, content, token).ConfigureAwait(false);
        _written[path] = content.Length;
        progress?.Report(1);
        return (Files.Open(info), info.ReducedImages);
    }

    public async Task ExtractAsync(IReadOnlyList<PageEntry> pages, string targetPath, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var info = await Files.Api.ExtractAsync(new ExtractRequest(Path.GetFileName(targetPath), ToRemote(pages)), token).ConfigureAwait(false);
        try
        {
            var content = await Files.Api.DownloadAsync(info.Id, token).ConfigureAwait(false);
            await targets.WriteAsync(targetPath, content, token).ConfigureAwait(false);
        }
        finally
        {
            await Files.Api.DeleteAsync(info.Id).ConfigureAwait(false);
        }
        progress?.Report(1);
    }

    public async Task<int> SplitAsync(IReadOnlyList<PageEntry> pages, IReadOnlyList<SplitPart> parts, string target, string baseName,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        progress?.Report(0.1);
        var zip = await Files.Api.SplitAsync(new SplitDocumentRequest(ToRemote(pages), parts, baseName), token).ConfigureAwait(false);
        progress?.Report(0.8);
        await targets.WriteAsync(target, zip, token).ConfigureAwait(false);
        progress?.Report(1);
        return parts.Count;
    }

    public static string IdOf(IPdfSource source) => source is RemotePdfSource remote
        ? remote.Id
        : throw new NotSupportedException($"{source.GetType().Name} is not a file on the server.");

    /// <summary>The page list as the server needs it, with everything placed on the pages.</summary>
    public static IReadOnlyList<RemotePage> ToRemote(IReadOnlyList<PageEntry> pages) =>
        [.. pages.Select(p => new RemotePage(IdOf(p.Source), p.SourceIndex, p.ExtraRotation,
            p.Stamps.Count == 0 ? null : [.. p.Stamps.Select(ToRemote)],
            p.Texts.Count == 0 ? null : p.Texts,
            p.Redactions.Count == 0 ? null : p.Redactions,
            p.OcrWords.Count == 0 ? null : p.OcrWords))];

    private static RemoteStamp ToRemote(StampOverlay stamp) => new(stamp.Id, stamp.PixelWidth, stamp.PixelHeight, stamp.Rect, stamp.Rotation,
        stamp.CoversContent, stamp.Bgra ?? throw new NotSupportedException("A placed stamp has no decoded picture and cannot be saved."));
}
