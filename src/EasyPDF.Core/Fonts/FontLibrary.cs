using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EasyPDF.Core.Fonts;

/// <summary>
/// The fonts installed on this computer that can be embedded into a PDF (<see cref="FontFile"/>): the system's font
/// folders, read once on first use. Placed text in such a font is shown on screen by its family name and embedded when
/// the document is saved (<c>PdfSaver</c>); where the font is missing (another computer, the web server) the most similar
/// standard font takes its place. A browser has no font folders, so the web version only ever uses standard fonts.
/// </summary>
public sealed class FontLibrary
{
    private readonly List<string> _folders;
    private readonly object _lock = new();
    private List<FontFace>? _faces;

    public FontLibrary(IEnumerable<string> folders) => _folders = [.. folders];

    /// <summary>The fonts of this computer (and folders added with <see cref="AddFolder"/>).</summary>
    public static FontLibrary Shared { get; } = new(SystemFolders());

    /// <summary>Adds a folder of fonts (tests; read again on the next search).</summary>
    public void AddFolder(string folder)
    {
        lock (_lock)
        {
            if (!_folders.Contains(folder))
            {
                _folders.Add(folder);
                _faces = null;
            }
        }
    }

    /// <summary>The system's font folders on Windows, macOS and Linux (only those that exist are read).</summary>
    public static IEnumerable<string> SystemFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(home, "Library", "Fonts");
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(home, ".local", "share", "fonts");
            yield return Path.Combine(home, ".fonts");
        }
    }

    /// <summary>Every font face found, read on the first call.</summary>
    public IReadOnlyList<FontFace> Faces
    {
        get
        {
            lock (_lock)
                return _faces ??= Scan(_folders);
        }
    }

    private const int MaxFiles = 5000;

    private static List<FontFace> Scan(IEnumerable<string> folders)
    {
        var faces = new List<FontFace>();
        var files = 0;
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var path in paths)
            {
                var extension = Path.GetExtension(path);
                if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (++files > MaxFiles)
                    return faces;
                if (FontFile.Read(path) is { } face && !faces.Any(f => f.PostScriptName == face.PostScriptName))
                    faces.Add(face);
            }
        }
        return faces;
    }

    /// <summary>The installed face of a family in a style (closest style if that one is missing); null if the family is not installed.</summary>
    public FontFace? Find(string family, bool bold, bool italic)
    {
        var key = Normalize(family);
        return Closest(Faces.Where(f => Normalize(f.Family) == key), bold, italic);
    }

    /// <summary>
    /// The installed face for a font named in a PDF: first by its PostScript name (what PDFs normally name a font by,
    /// e.g. "Arial-BoldMT"), then by family (from the name before "-"/"," and from <paramref name="family"/>) and style.
    /// </summary>
    public FontFace? Match(string baseFont, string family, bool bold, bool italic)
    {
        var name = PageFont.WithoutSubsetPrefix(baseFont);
        var faces = Faces;
        if (faces.FirstOrDefault(f => string.Equals(f.PostScriptName, name, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;
        var cut = name.IndexOfAny(['-', ',']);
        foreach (var candidate in new[] { cut > 0 ? name[..cut] : name, family })
        {
            var key = Normalize(candidate);
            if (key.Length == 0)
                continue;
            if (Closest(faces.Where(f => Normalize(f.Family) == key || Normalize(f.Family.Replace(" ", "")) == key), bold, italic) is { } face)
                return face;
        }
        return null;
    }

    private static FontFace? Closest(IEnumerable<FontFace> faces, bool bold, bool italic) =>
        faces.OrderBy(f => (f.Bold == bold ? 0 : 2) + (f.Italic == italic ? 0 : 1))
            .ThenBy(f => Math.Abs(f.Weight - (bold ? 700 : 400)))  // Regular before Medium, Bold before SemiBold
            .ThenBy(f => f.Style.Length)                           // "Regular" before "Regular Condensed"
            .FirstOrDefault();

    /// <summary>
    /// A family name for comparing: letters and digits only, in lower case, without the "PS"/"MT" endings of PostScript
    /// names ("TimesNewRomanPSMT" → "timesnewroman", "Times New Roman" → "timesnewroman").
    /// </summary>
    public static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        var s = sb.ToString();
        foreach (var ending in new[] { "psmt", "mt", "ps" })
            if (s.Length > ending.Length + 2 && s.EndsWith(ending, StringComparison.Ordinal))
                return s[..^ending.Length];
        return s;
    }
}
