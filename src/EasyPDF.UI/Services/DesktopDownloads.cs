using System;
using System.Collections.Generic;
using EasyPDF.Core.Downloads;

namespace EasyPDF.App.Services;

/// <summary>
/// Web version: the desktop packages the server offers (button in the brand header). Null on the desktop and when the
/// server has none, then the button is hidden.
/// </summary>
public static class DesktopDownloads
{
    public static WebPackages? Web { get; set; }

    public static bool IsAvailable => Web is { Packages.Count: > 0 };

    /// <param name="Packages">Newest package of each kind.</param>
    /// <param name="ThisSystem">The kinds that fit the browser's system (marked "for your system"); empty if unknown.</param>
    /// <param name="Download">Starts the download of a package in the browser.</param>
    public sealed record WebPackages(IReadOnlyList<DesktopPackage> Packages, IReadOnlySet<DesktopPackageKind> ThisSystem, Action<DesktopPackage> Download);

    /// <summary>The kinds for a system as the browser names it (<c>navigator.userAgentData.platform</c> or the user agent).</summary>
    public static IReadOnlySet<DesktopPackageKind> KindsFor(string platform)
    {
        var p = platform.ToLowerInvariant();
        if (p.Contains("android") || p.Contains("iphone") || p.Contains("ipad"))
            return new HashSet<DesktopPackageKind>();
        if (p.Contains("win"))
            return new HashSet<DesktopPackageKind> { DesktopPackageKind.WindowsInstaller, DesktopPackageKind.WindowsPortable };
        if (p.Contains("mac"))
            return new HashSet<DesktopPackageKind> { DesktopPackageKind.MacOS };
        if (p.Contains("linux") || p.Contains("x11"))
            return new HashSet<DesktopPackageKind> { DesktopPackageKind.LinuxDeb, DesktopPackageKind.LinuxTar };
        return new HashSet<DesktopPackageKind>();
    }
}
