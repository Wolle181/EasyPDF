using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using EasyPDF.Core.Templates;

namespace EasyPDF.Core.Stamps;

/// <summary>
/// Stamps and templates in one ZIP file (web version: the user's own ones in the browser's storage and as export, the
/// server's samples and shared ones): PNG files below <c>stamps/</c> (one level of subfolders, as in the stamp folder) and
/// text files below <c>templates/</c>. Reading accepts only such plain names: nothing can land outside the target folder.
/// </summary>
public static class LibraryArchive
{
    public const string Stamps = "stamps/";
    public const string Templates = "templates/";

    /// <summary>A single file larger than this is skipped when reading.</summary>
    public const long MaxFileBytes = 5L * 1024 * 1024;

    private static readonly char[] InvalidNameChars = [.. Path.GetInvalidFileNameChars().Union("<>:\"/\\|?*")];

    /// <summary>
    /// The stamps of <paramref name="stampFolder"/> and the templates of <paramref name="templateFolder"/> as a ZIP file;
    /// <paramref name="include"/> can leave files out (by full path). Both folders are in it also when empty, so that
    /// reading it back gives empty folders, not missing ones.
    /// </summary>
    public static byte[] Pack(string stampFolder, string templateFolder, Func<string, bool>? include = null)
    {
        include ??= _ => true;
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry(Stamps);
            archive.CreateEntry(Templates);
            foreach (var group in StampLibrary.ListGroups(stampFolder))
                foreach (var file in group.Files.Where(include))
                    Add(archive, Stamps + (group.Name is { } name ? name + "/" : "") + Path.GetFileName(file), file, CompressionLevel.NoCompression);
            foreach (var template in TemplateLibrary.Load(templateFolder))
                if (template.FilePath is { } file && include(file))
                    Add(archive, Templates + Path.GetFileName(file), file, CompressionLevel.Optimal);
        }
        return zip.ToArray();
    }

    private static void Add(ZipArchive archive, string name, string file, CompressionLevel level)
    {
        try
        {
            var content = File.ReadAllBytes(file);
            using var target = archive.CreateEntry(name, level).Open();
            target.Write(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that can't be read is left out, the others are kept
        }
    }

    /// <summary>
    /// Writes the files below <paramref name="prefix"/> with <paramref name="extension"/> into <paramref name="folder"/>
    /// (files of the same name are replaced), those of one level of subfolders into subfolders if
    /// <paramref name="subfolders"/>. The folder is created when the ZIP has anything below the prefix, even only the
    /// prefix itself. Returns the number of files written. Throws <see cref="InvalidDataException"/> if it isn't a ZIP file.
    /// </summary>
    public static int Extract(byte[] zip, string prefix, string folder, string extension, bool subfolders)
    {
        var count = 0;
        using var archive = Open(zip);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            Directory.CreateDirectory(folder);
            var parts = entry.FullName[prefix.Length..].Split('/');
            if (parts.Length > (subfolders ? 2 : 1) || !parts.All(IsPlainName)
                || !Path.GetExtension(parts[^1]).Equals(extension, StringComparison.OrdinalIgnoreCase) || entry.Length > MaxFileBytes)
                continue;
            var target = Path.Combine([folder, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var source = entry.Open())
            using (var output = File.Create(target))
                source.CopyTo(output);
            count++;
        }
        return count;
    }

    /// <summary>The names of the subfolders directly below <paramref name="prefix"/> (only plain names).</summary>
    public static IReadOnlyList<string> Subfolders(byte[] zip, string prefix)
    {
        using var archive = Open(zip);
        return [.. archive.Entries
            .Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal))
            .Select(e => e.FullName[prefix.Length..].Split('/'))
            .Where(parts => parts.Length >= 2 && IsPlainName(parts[0]))
            .Select(parts => parts[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    private static ZipArchive Open(byte[] zip)
    {
        try
        {
            return new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        }
        catch (Exception ex) when (ex is not InvalidDataException and (ArgumentException or IOException))
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    /// <summary>A file or folder name without path parts and without characters a file name can't have on any system.</summary>
    private static bool IsPlainName(string name) =>
        name.Length > 0 && name.Trim() == name && name != "." && name != ".." && !name.EndsWith('.')
        && !name.Any(c => char.IsControl(c) || InvalidNameChars.Contains(c));
}
