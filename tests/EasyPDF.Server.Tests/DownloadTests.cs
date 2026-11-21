using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using EasyPDF.Core.Downloads;
using EasyPDF.Remote;
using Xunit;

namespace EasyPDF.Server.Tests;

/// <summary>The desktop packages the web version offers for download (folder next to the container).</summary>
public class DownloadTests
{
    [Theory]
    [InlineData("App-1.0.0.18-Windows-Installer.msi", DesktopPackageKind.WindowsInstaller, "1.0.0.18")]
    [InlineData("App-1.0.0.18-Windows-portable.zip", DesktopPackageKind.WindowsPortable, "1.0.0.18")]
    [InlineData("My App-2.1.0.5-macOS-AppleSilicon.dmg", DesktopPackageKind.MacOS, "2.1.0.5")]
    [InlineData("App-1.0.0.18-Linux-x64.deb", DesktopPackageKind.LinuxDeb, "1.0.0.18")]
    [InlineData("App-1.0.0.18-linux-x64.tar.gz", DesktopPackageKind.LinuxTar, "1.0.0.18")]
    public void PackagesAreRecognizedByTheirName(string name, DesktopPackageKind kind, string version)
    {
        var package = DesktopPackage.FromFile(name, 1);
        Assert.Equal(kind, package?.Kind);
        Assert.Equal(version, package?.Version);
    }

    [Theory]
    [InlineData("App-1.0.0.18-win-x64.msi")]
    [InlineData("notes.txt")]
    [InlineData("App-Windows-Installer.msi")]
    [InlineData("App-1.0.0.18-Windows-Installer.msi.part")]
    public void OtherFilesAreNoPackages(string name) => Assert.Null(DesktopPackage.FromFile(name, 1));

    [Fact]
    public void TheInstallerScriptsNameThePackagesSo()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "EasyPDF.slnx")))
            root = Path.GetDirectoryName(root)!;
        string Read(string path) => File.ReadAllText(Path.Combine(root, path));
        Assert.Contains("-" + Path.GetFileNameWithoutExtension(DesktopPackage.Suffixes[DesktopPackageKind.WindowsInstaller]) + "</OutputName>",
            Read("installer/windows/EasyPDF.Installer.wixproj"));
        Assert.Contains(DesktopPackage.Suffixes[DesktopPackageKind.WindowsPortable], Read("installer/windows/build-windows-on-windows.ps1"));
        Assert.Contains(DesktopPackage.Suffixes[DesktopPackageKind.MacOS], Read("installer/build-macos-on-macos.sh"));
        var linux = Read("installer/linux/build_linux.py");
        Assert.Contains(DesktopPackage.Suffixes[DesktopPackageKind.LinuxDeb], linux);
        Assert.Contains(DesktopPackage.Suffixes[DesktopPackageKind.LinuxTar], linux);
    }

    [Fact]
    public async Task TheServerListsTheNewestOfEachKindAndSendsIt()
    {
        await using var server = await TestServerHost.StartAsync();
        var folder = Path.Combine(server.Folder, "downloads");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "App-1.0.0.9-Windows-Installer.msi"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "App-1.0.0.18-Windows-Installer.msi"), [1, 2]);
        File.WriteAllBytes(Path.Combine(folder, "App-1.0.0.18-Linux-x64.deb"), [3]);
        File.WriteAllBytes(Path.Combine(folder, "App-1.0.0.18-macOS-AppleSilicon.dmg"), [4]);
        File.WriteAllText(Path.Combine(folder, "secret.txt"), "not for download");
        var browser = server.NewBrowser();

        var list = (await (await browser.GetAsync("/api/downloads")).Content.ReadFromJsonAsync(RemoteJson.Default.IReadOnlyListDesktopPackage))!;
        Assert.Equal(["App-1.0.0.18-Windows-Installer.msi", "App-1.0.0.18-macOS-AppleSilicon.dmg", "App-1.0.0.18-Linux-x64.deb"],
            list.Select(p => p.Name));
        Assert.Equal(2, list[0].Size);

        var response = await browser.GetAsync("/api/" + RemoteApi.DownloadUrl(list[0])["api/".Length..]);
        response.EnsureSuccessStatusCode();
        Assert.Equal([1, 2], await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("App-1.0.0.18-Windows-Installer.msi", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        // Only listed packages: no other file of the folder, no older version, no paths
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/downloads/secret.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/downloads/App-1.0.0.9-Windows-Installer.msi")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/downloads/..%2Fkeys%2Fx")).StatusCode);
    }

    [Fact]
    public async Task WithoutFolderThereAreNoDownloads()
    {
        await using var server = await TestServerHost.StartAsync();
        var list = await (await server.NewBrowser().GetAsync("/api/downloads")).Content.ReadFromJsonAsync(RemoteJson.Default.IReadOnlyListDesktopPackage);
        Assert.Empty(list!);
    }
}
