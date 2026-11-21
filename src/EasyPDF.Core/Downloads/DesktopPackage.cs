using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EasyPDF.Core.Downloads;

/// <summary>The kinds of desktop packages the installer scripts build, in the order the web version lists them.</summary>
public enum DesktopPackageKind
{
    WindowsInstaller,
    WindowsPortable,
    MacOS,
    LinuxDeb,
    LinuxTar,
}

/// <summary>
/// A desktop package the web version offers for download, e.g. <c>&lt;App&gt;-1.0.0.18-Windows-Installer.msi</c>
/// (names: <see cref="FileName"/>).
/// </summary>
public sealed record DesktopPackage(string Name, long Size, DesktopPackageKind Kind, string Version)
{
    /// <summary>The end of the file name per kind, after <c>&lt;App&gt;-&lt;version&gt;-</c>.</summary>
    public static IReadOnlyDictionary<DesktopPackageKind, string> Suffixes { get; } = new Dictionary<DesktopPackageKind, string>
    {
        [DesktopPackageKind.WindowsInstaller] = "Windows-Installer.msi",
        [DesktopPackageKind.WindowsPortable] = "Windows-portable.zip",
        [DesktopPackageKind.MacOS] = "macOS-AppleSilicon.dmg",
        [DesktopPackageKind.LinuxDeb] = "Linux-x64.deb",
        [DesktopPackageKind.LinuxTar] = "Linux-x64.tar.gz",
    };

    private static readonly Regex NamePattern = new(@"^.+?-(?<version>\d+(\.\d+){1,3})-(?<suffix>.+)$", RegexOptions.CultureInvariant);

    /// <summary>The file name the installer scripts give a package: <c>&lt;App&gt;-&lt;version&gt;-&lt;suffix&gt;</c>.</summary>
    public static string FileName(string appName, string version, DesktopPackageKind kind) => $"{appName}-{version}-{Suffixes[kind]}";

    /// <summary>The package a file is (by its name); null for any other file.</summary>
    public static DesktopPackage? FromFile(string name, long size)
    {
        var match = NamePattern.Match(name);
        if (!match.Success)
            return null;
        foreach (var (kind, suffix) in Suffixes)
            if (string.Equals(match.Groups["suffix"].Value, suffix, StringComparison.OrdinalIgnoreCase))
                return new DesktopPackage(name, size, kind, match.Groups["version"].Value);
        return null;
    }

    /// <summary>The newest package of each kind, in the order of <see cref="DesktopPackageKind"/>.</summary>
    public static IReadOnlyList<DesktopPackage> Newest(IEnumerable<DesktopPackage> packages) =>
        [.. packages.GroupBy(p => p.Kind).OrderBy(g => g.Key)
            .Select(g => g.OrderByDescending(p => System.Version.TryParse(p.Version, out var v) ? v : new System.Version()).ThenByDescending(p => p.Name, StringComparer.Ordinal).First())];
}
