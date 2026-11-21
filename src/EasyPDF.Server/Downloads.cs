using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EasyPDF.Core.Downloads;

namespace EasyPDF.Server;

/// <summary>
/// The desktop packages in the downloads folder (filled next to the container, e.g. from the release of the installers):
/// only files named like the installer scripts name them, the newest of each kind.
/// </summary>
public static class Downloads
{
    public static IReadOnlyList<DesktopPackage> List(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return [];
        return DesktopPackage.Newest(new DirectoryInfo(folder).EnumerateFiles()
            .Select(f => DesktopPackage.FromFile(f.Name, f.Length)).OfType<DesktopPackage>());
    }

    /// <summary>The path of a listed package; null for any other name (no other file of the folder, no paths).</summary>
    public static string? Find(string folder, string name) =>
        List(folder).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal)) is { } package
            ? Path.Combine(folder, package.Name)
            : null;
}
