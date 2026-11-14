using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Templates;

namespace EasyPDF.Core.Stamps;

/// <summary>The user's stamp folder: a normal folder with PNG pictures (signatures, company stamps, logos ...). Stamps are PNG only: transparency and fields (<see cref="StampFile"/>) need it.</summary>
public static class StampLibrary
{
    private const string Extension = ".png";

    /// <summary>Documents/&lt;app name&gt;/Stamps (falls back to the home folder where there is no Documents folder).</summary>
    public static string DefaultFolder => AppInfo.DocumentsFolder("Stamps");

    /// <summary>The configured stamp folder, or <see cref="DefaultFolder"/> while none is set.</summary>
    public static string ResolveFolder(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.StampFolder) ? DefaultFolder : settings.StampFolder;

    /// <summary>The sample stamps shipped next to the program (sample-stamps/), so the feature can be tried at once.</summary>
    public static string SampleFolder => Path.Combine(AppContext.BaseDirectory, "sample-stamps");

    /// <summary>Creates the folder if it is missing; false if that is not possible (e.g. no permission).</summary>
    public static bool TryEnsureFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the folder if needed and copies each sample of <paramref name="sampleFolder"/> into it that is not in
    /// <paramref name="provided"/> yet ("stamp:" + path in the sample folder, e.g. "stamp:Numbers/01.png"), then adds it
    /// there: a sample the user deleted doesn't come back, one shipped by a later version still arrives, also in a folder
    /// that exists already. A folder that doesn't exist (any more) starts over with all samples, whatever the settings kept
    /// from earlier say. Samples in a subfolder go into a subfolder of the same name, or the one
    /// <paramref name="subfolderName"/> gives (e.g. in the program's language). A file of the same name in the folder is
    /// kept. True if <paramref name="provided"/> changed; a failed copy is tried again next time.
    /// </summary>
    public static bool ProvideSamples(string folder, string sampleFolder, ICollection<string> provided, Func<string, string>? subfolderName = null)
    {
        var startOver = !Directory.Exists(folder);
        if (!TryEnsureFolder(folder))
            return false;
        var samples = ListImages(sampleFolder).Select(f => (Key: Path.GetFileName(f), File: f, Target: folder)).ToList();
        foreach (var sub in ListSubfolders(sampleFolder))
        {
            var name = Path.GetFileName(sub);
            var target = Path.Combine(folder, subfolderName?.Invoke(name) ?? name);
            samples.AddRange(ListImages(sub).Select(f => (Key: name + "/" + Path.GetFileName(f), File: f, Target: target)));
        }
        var changed = false;
        foreach (var (relative, file, target) in samples)
        {
            var key = "stamp:" + relative;
            var known = provided.Contains(key);
            if ((known && !startOver) || !TryEnsureFolder(target))
                continue;
            try
            {
                var path = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(path))
                    File.Copy(file, path);
                if (!known)
                {
                    provided.Add(key);
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder stays usable without the sample
            }
        }
        return changed;
    }

    /// <summary>
    /// The pictures of the folder for the gallery: first those directly in it (<see cref="StampGroup.Name"/> null, also
    /// when there are none), then one group per subfolder with pictures, sorted by name (one level deep).
    /// </summary>
    public static IReadOnlyList<StampGroup> ListGroups(string folder)
    {
        var groups = new List<StampGroup> { new(null, ListImages(folder)) };
        foreach (var sub in ListSubfolders(folder))
        {
            var files = ListImages(sub);
            if (files.Count > 0)
                groups.Add(new StampGroup(Path.GetFileName(sub), files));
        }
        return groups;
    }

    private static IReadOnlyList<string> ListSubfolders(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return [];
            return [.. Directory.EnumerateDirectories(folder).OrderBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes a stamp as a PNG named after <paramref name="name"/> (characters a file name can't have become "_", a name in
    /// use gets " (2)"), through a temporary file so that a failure leaves the old stamp alone. <paramref name="replaces"/>
    /// is the file it takes the place of: that name counts as free, and if the name changed the old file goes. Returns the
    /// path written. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if that isn't possible.
    /// </summary>
    public static string SaveStamp(string folder, string name, byte[] png, string? replaces = null)
    {
        Directory.CreateDirectory(folder);
        var used = new HashSet<string>(Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
        if (replaces != null)
            used.Remove(Path.GetFileName(replaces));
        var target = Path.Combine(folder, TemplateLibrary.UniqueFileName(name, used, Extension));
        var temporary = target + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, png);
            // Delete first: on Windows and macOS a rename that only changes the case would otherwise delete the renamed file
            if (replaces != null && replaces != target && File.Exists(replaces))
                File.Delete(replaces);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return target;
    }

    /// <summary>
    /// The resolution stored in a PNG (pHYs) in dots per inch; null if it has none. A stamp is
    /// placed in the size this gives, so a picture can be sharp (many pixels) and still come in a small size.
    /// </summary>
    public static double? ReadDpi(ReadOnlySpan<byte> data)
    {
        double? dpi = null;
        if (data.Length > 8 && data[..8].SequenceEqual(PngSignature))
        {
            for (var pos = 8; pos + 8 <= data.Length;)
            {
                var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
                var type = data.Slice(pos + 4, 4);
                if (type.SequenceEqual("IDAT"u8) || length < 0 || pos + 12 + length > data.Length)
                    break;
                if (type.SequenceEqual("pHYs"u8) && length >= 9 && data[pos + 16] == 1) // unit 1 = metre
                    dpi = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 8)..]) * 0.0254;
                pos += 12 + length;
            }
        }
        return dpi is >= 10 and <= 10000 ? dpi : null;
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>PNG files of the folder sorted by name; a missing or unreadable folder yields an empty list.</summary>
    public static IReadOnlyList<string> ListImages(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return [];
            return [.. Directory.EnumerateFiles(folder)
                .Where(f => Path.GetExtension(f).Equals(Extension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Pictures of the stamp folder shown together in the gallery: <see cref="Name"/> is the subfolder, null for the folder itself.</summary>
public sealed record StampGroup(string? Name, IReadOnlyList<string> Files);
