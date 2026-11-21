using System.IO;
using EasyPDF.Core.Downloads;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>
/// The installer scripts name the packages as the web version expects them in its downloads folder. Here and not in the
/// server tests: those also run in the container build, which has no installer scripts.
/// </summary>
public class DesktopPackageNameTests
{
    [Fact]
    public void TheInstallerScriptsNameThePackagesSo()
    {
        static string Read(string path) => File.ReadAllText(Path.Combine(RepoPaths.Root, path));
        Assert.Contains("-" + Path.GetFileNameWithoutExtension(DesktopPackage.Suffixes[DesktopPackageKind.WindowsInstaller]) + "</OutputName>",
            Read("installer/windows/EasyPDF.Installer.wixproj"));
        Assert.Contains(DesktopPackage.Suffixes[DesktopPackageKind.WindowsPortable], Read("installer/windows/build-windows-on-windows.ps1"));
        Assert.Contains(DesktopPackage.Suffixes[DesktopPackageKind.MacOS], Read("installer/build-macos-on-macos.sh"));
        var linux = Read("installer/linux/build_linux.py");
        Assert.Contains(DesktopPackage.Suffixes[DesktopPackageKind.LinuxDeb], linux);
        Assert.Contains(DesktopPackage.Suffixes[DesktopPackageKind.LinuxTar], linux);
    }
}
