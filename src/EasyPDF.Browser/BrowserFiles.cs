using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using EasyPDF.App.Services;
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
