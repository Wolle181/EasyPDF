using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace EasyPDF.Server.Storage;

/// <summary>
/// The stamps and templates the browser program gets at start, as one ZIP file: the sample stamps shipped with the server
/// (<c>samples/</c>, with subfolders like <c>samples/Numbers/</c>) and the shared folder of the installation
/// (<c>shared/stamps/</c> with one level of subfolders, <c>shared/templates/*.txt</c>), read-only for the users.
/// </summary>
public static class Library
{
    /// <summary>A single file larger than this is left out (a stamp is a small picture, a template a short text).</summary>
    public const long MaxFileBytes = 5L * 1024 * 1024;

    /// <summary>The whole ZIP stays below this; files beyond it are left out.</summary>
    public const long MaxTotalBytes = 50L * 1024 * 1024;

    /// <summary>The sample stamps next to the server (the same as next to the desktop program).</summary>
    public static string SampleFolder => Path.Combine(AppContext.BaseDirectory, "sample-stamps");

    public static byte[] BuildZip(string sampleFolder, string sharedFolder)
    {
        var entries = new List<(string Name, string File)>();
        entries.AddRange(Pictures(sampleFolder, "samples/"));
        if (sharedFolder.Length > 0)
        {
            entries.AddRange(Pictures(Path.Combine(sharedFolder, "stamps"), "shared/stamps/"));
            entries.AddRange(Files(Path.Combine(sharedFolder, "templates"), ".txt").Select(f => ("shared/templates/" + Path.GetFileName(f), f)));
        }
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            long total = 0;
            foreach (var (name, file) in entries)
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    if (length > MaxFileBytes || total + length > MaxTotalBytes)
                        continue;
                    total += length;
                    // PNG is compressed already
                    var entry = archive.CreateEntry(name, name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.Fastest : CompressionLevel.NoCompression);
                    using var target = entry.Open();
                    using var source = File.OpenRead(file);
                    source.CopyTo(target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable file is left out, the others still come
                }
            }
        }
        return zip.ToArray();
    }

    /// <summary>The PNG files of the folder and of its subfolders (one level), named prefix + relative path.</summary>
    private static IEnumerable<(string Name, string File)> Pictures(string folder, string prefix)
    {
        foreach (var file in Files(folder, ".png"))
            yield return (prefix + Path.GetFileName(file), file);
        foreach (var sub in Folders(folder))
            foreach (var file in Files(sub, ".png"))
                yield return (prefix + Path.GetFileName(sub) + "/" + Path.GetFileName(file), file);
    }

    private static IReadOnlyList<string> Files(string folder, string extension)
    {
        try
        {
            return Directory.Exists(folder)
                ? [.. Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> Folders(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? [.. Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
