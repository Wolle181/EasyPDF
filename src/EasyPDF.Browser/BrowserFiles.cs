using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using EasyPDF.App.Services;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using EasyPDF.Remote;

namespace EasyPDF.Browser;

/// <summary>
/// Picked or dropped files are uploaded to the server (the program then works with their virtual path); a file chosen in
/// the save dialog becomes a target the finished PDF is written to.
/// </summary>
internal sealed class BrowserFiles(RemoteFiles files, BrowserSaveTargets targets) : IPlatformFiles
{
    public async Task<string?> ToOpenPathAsync(IStorageFile file)
    {
        var properties = await file.GetBasicPropertiesAsync();
        await using var stream = await file.OpenReadAsync();
        try
        {
            var info = await files.Api.UploadAsync(stream, file.Name, (long?)properties.Size);
            return files.Add(info);
        }
        catch (RemoteApiException ex)
        {
            await UploadErrors.ShowAsync(ex, file.Name);
            return null;
        }
    }

    public string? ToSavePath(IStorageFile file) => targets.Add(file);

    public Task WriteAsync(string path, byte[] content) => targets.WriteAsync(path, content, CancellationToken.None);

    public async Task<bool> ShowForPrintingAsync(IReadOnlyList<PageEntry> pages, string name)
    {
        var info = await files.Api.ExtractAsync(new ExtractRequest(name, RemotePdfBackend.ToRemote(pages)), CancellationToken.None);
        // The server's own address: the tab shows the PDF in the browser's viewer (the file expires like every other)
        return Interop.OpenTab($"api/files/{info.Id}/content?inline=true");
    }

    public async Task<string> PrepareForSharingAsync(EditableDocument document, string name)
    {
        var request = new BuildRequest(RemotePdfBackend.IdOf(document.BaseSource), name, RemotePdfBackend.ToRemote(document.Pages));
        var info = await files.Api.BuildAsync(request, CancellationToken.None);
        return $"api/files/{info.Id}/content";
    }

    public Task<bool> ShowXmlAttachmentAsync(IPdfSource source, PdfAttachment attachment) =>
        Task.FromResult(Interop.OpenTab($"api/files/{RemotePdfBackend.IdOf(source)}/attachments/{attachment.Index}?view=true"));

    public async Task<ShareOutcome> ShareAsync(string prepared, string name) =>
        await Interop.ShareFile(prepared, name, Path.GetFileNameWithoutExtension(name)) switch
        {
            "shared" => ShareOutcome.Shared,
            "cancelled" => ShareOutcome.Cancelled,
            _ => ShareOutcome.NeedsClick,
        };
}

/// <summary>The files the user chose in save dialogs, by a virtual path <c>/save/&lt;id&gt;/&lt;name&gt;</c>.</summary>
internal sealed class BrowserSaveTargets : ISaveTargets
{
    private readonly ConcurrentDictionary<string, IStorageFile> _targets = new(StringComparer.Ordinal);

    public string Add(IStorageFile file)
    {
        var path = $"/save/{Guid.NewGuid():N}/{file.Name}";
        _targets[path] = file;
        return path;
    }

    public bool Has(string path) => _targets.ContainsKey(path);

    public async Task WriteAsync(string path, byte[] content, CancellationToken token)
    {
        if (!_targets.TryGetValue(path, out var file))
            throw new IOException("No target chosen for " + Path.GetFileName(path));
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(content, token);
        await stream.FlushAsync(token);
    }
}
