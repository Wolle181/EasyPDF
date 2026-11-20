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
        var info = await Files.Api.BuildAsync(new BuildRequest(IdOf(baseSource), name, ToRemote(pages)), token).ConfigureAwait(false);
        progress?.Report(0.6);
        var content = await Files.Api.DownloadAsync(info.Id, token).ConfigureAwait(false);
        progress?.Report(0.9);
        await targets.WriteAsync(path, content, token).ConfigureAwait(false);
        progress?.Report(1);
        return (Files.Open(info), 0);
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

    private static string IdOf(IPdfSource source) => source is RemotePdfSource remote
        ? remote.Id
        : throw new NotSupportedException($"{source.GetType().Name} is not a file on the server.");

    /// <summary>The page list as the server needs it (overlays are not part of the web version yet: the pages are written without them).</summary>
    public static IReadOnlyList<RemotePage> ToRemote(IReadOnlyList<PageEntry> pages)
    {
        if (pages.Any(p => p.Stamps.Count > 0 || p.Texts.Count > 0 || p.Redactions.Count > 0 || p.OcrWords.Count > 0))
            throw new NotSupportedException("Stamps, texts, redactions and recognized text are not supported by the web version yet.");
        return [.. pages.Select(p => new RemotePage(IdOf(p.Source), p.SourceIndex, p.ExtraRotation))];
    }
}
