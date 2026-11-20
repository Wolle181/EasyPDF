using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasyPDF.Server.Storage;

/// <summary>A PDF on the server: uploaded, or written by the server for its owner.</summary>
public sealed class StoredFile(string id, string owner, string name, string path, long size, DateTimeOffset created)
{
    public string Id { get; } = id;
    public string Owner { get; } = owner;
    public string Name { get; } = name;
    public string Path { get; } = path;
    public long Size { get; } = size;
    public DateTimeOffset Created { get; } = created;
    public DateTimeOffset LastAccess { get; set; } = created;
}

public sealed class QuotaExceededException : Exception;

/// <summary>
/// The files on the server, per owner (<see cref="Security.OwnerKeys"/>). Ids are random 128-bit values; a file is only
/// found together with its owner, so a guessed or foreign id is simply "not found". Files are temporary: they go when
/// their owner deletes them, after <see cref="StorageOptions.IdleTimeoutMinutes"/> without use or at the latest after
/// <see cref="StorageOptions.MaxAgeHours"/>, and all of them at start-up (the server keeps no list across restarts).
/// </summary>
public sealed class FileStore
{
    private readonly ConcurrentDictionary<string, StoredFile> _files = new(StringComparer.Ordinal);
    private readonly StorageOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FileStore> _logger;

    public FileStore(IOptions<ServerOptions> options, TimeProvider time, ILogger<FileStore> logger)
    {
        _options = options.Value.Storage;
        _time = time;
        _logger = logger;
        Directory.CreateDirectory(_options.Folder);
    }

    public int Count => _files.Count;

    public long TotalBytes => _files.Values.Sum(f => f.Size);

    public long OwnerBytes(string owner) => _files.Values.Where(f => f.Owner == owner).Sum(f => f.Size);

    /// <summary>Deletes whatever an earlier run left behind (its files can no longer be reached).</summary>
    public void DeleteLeftovers()
    {
        foreach (var dir in Directory.EnumerateDirectories(_options.Folder))
            TryDeleteDirectory(dir);
        foreach (var file in Directory.EnumerateFiles(_options.Folder))
            TryDeleteFile(file);
    }

    /// <summary>A new, not yet registered path in the owner's folder (for files the server writes).</summary>
    public string NewPath(string owner)
    {
        var folder = System.IO.Path.Combine(_options.Folder, owner);
        Directory.CreateDirectory(folder);
        return System.IO.Path.Combine(folder, NewId() + ".pdf");
    }

    /// <summary>Takes over a finished file at <paramref name="path"/> (from <see cref="NewPath"/>) for the owner.</summary>
    public StoredFile Register(string owner, string path, string name)
    {
        var size = new FileInfo(path).Length;
        if (!HasRoom(owner, size))
        {
            TryDeleteFile(path);
            throw new QuotaExceededException();
        }
        var id = System.IO.Path.GetFileNameWithoutExtension(path);
        var file = new StoredFile(id, owner, CleanName(name), path, size, _time.GetUtcNow());
        _files[id] = file;
        return file;
    }

    /// <summary>Writes an upload to the owner's folder (at most <paramref name="maxBytes"/>).</summary>
    public async Task<string> WriteUploadAsync(string owner, Stream content, long maxBytes, CancellationToken token)
    {
        var path = NewPath(owner);
        try
        {
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, token)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new FileTooLargeException();
                await target.WriteAsync(buffer.AsMemory(0, read), token);
            }
            return path;
        }
        catch
        {
            TryDeleteFile(path);
            throw;
        }
    }

    /// <summary>The owner's file with this id (its idle time starts again), or null.</summary>
    public StoredFile? Get(string owner, string id)
    {
        if (!_files.TryGetValue(id, out var file) || file.Owner != owner)
            return null;
        file.LastAccess = _time.GetUtcNow();
        return file;
    }

    public StoredFile? Delete(string owner, string id)
    {
        if (!_files.TryGetValue(id, out var file) || file.Owner != owner || !_files.TryRemove(id, out _))
            return null;
        TryDeleteFile(file.Path);
        return file;
    }

    /// <summary>Removes the files that are too old or unused for too long; returns them.</summary>
    public StoredFile[] RemoveExpired()
    {
        var now = _time.GetUtcNow();
        var idle = TimeSpan.FromMinutes(Math.Max(1, _options.IdleTimeoutMinutes));
        var maxAge = TimeSpan.FromHours(Math.Max(1, _options.MaxAgeHours));
        var expired = _files.Values.Where(f => now - f.LastAccess > idle || now - f.Created > maxAge).ToArray();
        foreach (var file in expired)
            if (_files.TryRemove(file.Id, out _))
                TryDeleteFile(file.Path);
        if (expired.Length > 0)
            _logger.LogInformation("Deleted {Count} expired files", expired.Length);
        return expired;
    }

    /// <summary>Removes all files of an owner (expired session); returns them.</summary>
    public StoredFile[] RemoveOwner(string owner)
    {
        var files = _files.Values.Where(f => f.Owner == owner).ToArray();
        foreach (var file in files)
            if (_files.TryRemove(file.Id, out _))
                TryDeleteFile(file.Path);
        TryDeleteDirectory(System.IO.Path.Combine(_options.Folder, owner));
        return files;
    }

    public bool HasRoom(string owner, long size) =>
        OwnerBytes(owner) + size <= _options.MaxOwnerMegabytes * 1024L * 1024L
        && TotalBytes + size <= _options.MaxTotalMegabytes * 1024L * 1024L;

    /// <summary>Only the file name, at most 120 characters, ending in .pdf (it is shown and used for downloads).</summary>
    public static string CleanName(string? name)
    {
        var file = System.IO.Path.GetFileName((name ?? "").Replace('\\', '/'));
        file = new string([.. file.Where(c => !char.IsControl(c) && c != '/' && c != '"')]).Trim();
        if (string.IsNullOrWhiteSpace(file))
            file = "document.pdf";
        if (!file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            file += ".pdf";
        return file.Length > 120 ? file[..116] + ".pdf" : file;
    }

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    internal static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class FileTooLargeException : Exception;
