using System.Collections.Generic;
using System.IO;
using System.Linq;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Stamps;
using Xunit;

namespace EasyPDF.Core.Tests;

public class StampLibraryTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fpc-stamps-" + System.Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ListsOnlyPngSortedByNameAndIgnoresCase()
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in new[] { "b.PNG", "a.png", "c.jpg", "c.jpeg", "notes.txt", "d.pdf", "e.gif" })
            File.WriteAllBytes(Path.Combine(_dir, name), [1]);
        var names = StampLibrary.ListImages(_dir).Select(f => Path.GetFileName(f)!).ToArray();
        Assert.Equal(["a.png", "b.PNG"], names);
    }

    [Fact]
    public void MissingFolderIsEmptyAndCanBeCreated()
    {
        var folder = Path.Combine(_dir, "sub", "stamps");
        Assert.Empty(StampLibrary.ListImages(folder));
        Assert.True(StampLibrary.TryEnsureFolder(folder));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void SamplesArriveOnceAlsoInAFolderThatExistsAlready()
    {
        var samples = Path.Combine(_dir, "samples");
        Directory.CreateDirectory(samples);
        File.WriteAllBytes(Path.Combine(samples, "Stamp 'sample'.png"), [1]);
        File.WriteAllBytes(Path.Combine(samples, "readme.txt"), [1]);
        // The folder exists already, e.g. kept from an uninstalled version
        var folder = Path.Combine(_dir, "stamps");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "mine.png"), [2]);
        var provided = new List<string>();

        Assert.True(StampLibrary.ProvideSamples(folder, samples, provided));
        Assert.Equal(["Stamp 'sample'.png", "mine.png"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Order(System.StringComparer.Ordinal).ToArray());
        Assert.Equal(["stamp:Stamp 'sample'.png"], provided);

        // A sample the user deleted does not come back, a new one of a later version does
        File.Delete(Path.Combine(folder, "Stamp 'sample'.png"));
        Assert.False(StampLibrary.ProvideSamples(folder, samples, provided));
        Assert.Equal(["mine.png"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).ToArray());
        File.WriteAllBytes(Path.Combine(samples, "New.png"), [1]);
        Assert.True(StampLibrary.ProvideSamples(folder, samples, provided));
        Assert.True(File.Exists(Path.Combine(folder, "New.png")));

        // A missing folder is created
        var other = Path.Combine(_dir, "other");
        Assert.False(StampLibrary.ProvideSamples(other, samples, provided));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void TheProgramShipsTheSampleStampAndTheNumbers0To20()
    {
        var names = StampLibrary.ListImages(StampLibrary.SampleFolder).Select(f => Path.GetFileName(f)!).ToArray();
        Assert.Contains("Stempel 'gescannt'.png", names);
        var numbers = StampLibrary.ListImages(Path.Combine(StampLibrary.SampleFolder, "Numbers"));
        Assert.Equal(Enumerable.Range(0, 21).Select(n => $"{n:00}.png").ToArray(), numbers.Select(f => Path.GetFileName(f)!).ToArray());
        // Sharp in print and still placed small: 300 dpi stored in the picture
        Assert.All(numbers, f => Assert.Equal(300, StampLibrary.ReadDpi(File.ReadAllBytes(f))!.Value, 0));
    }

    [Fact]
    public void SamplesInSubfoldersGoIntoSubfoldersNamedByTheCaller()
    {
        var samples = Path.Combine(_dir, "samples");
        Directory.CreateDirectory(Path.Combine(samples, "Numbers"));
        File.WriteAllBytes(Path.Combine(samples, "top.png"), [1]);
        File.WriteAllBytes(Path.Combine(samples, "Numbers", "01.png"), [1]);
        File.WriteAllBytes(Path.Combine(samples, "Numbers", "02.png"), [1]);
        var folder = Path.Combine(_dir, "stamps");
        var provided = new List<string>();

        Assert.True(StampLibrary.ProvideSamples(folder, samples, provided, name => name == "Numbers" ? "Nummern" : name));
        Assert.Equal(["stamp:top.png", "stamp:Numbers/01.png", "stamp:Numbers/02.png"], provided);
        Assert.True(File.Exists(Path.Combine(folder, "Nummern", "02.png")));

        // The gallery groups: the folder itself first, then each subfolder with pictures
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        var groups = StampLibrary.ListGroups(folder);
        Assert.Equal(new string?[] { null, "Nummern" }, groups.Select(g => g.Name));
        Assert.Equal(["top.png"], groups[0].Files.Select(f => Path.GetFileName(f)!).ToArray());
        Assert.Equal(["01.png", "02.png"], groups[1].Files.Select(f => Path.GetFileName(f)!).ToArray());

        // Deleted ones don't come back, also not after the language (subfolder name) changed
        Directory.Delete(Path.Combine(folder, "Nummern"), recursive: true);
        Assert.False(StampLibrary.ProvideSamples(folder, samples, provided, name => name));
        Assert.False(Directory.Exists(Path.Combine(folder, "Numbers")));
    }

    [Fact]
    public void ReadsTheResolutionOfAPng()
    {
        // PNG with pHYs 3779 px/m (96 dpi), as Windows tools write it
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 9, .. "pHYs"u8, 0, 0, 0x0E, 0xC3, 0, 0, 0x0E, 0xC3, 1, 0, 0, 0, 0,
            0, 0, 0, 0, .. "IDAT"u8, 0, 0, 0, 0];
        Assert.Equal(96, StampLibrary.ReadDpi(png)!.Value, 0);
        png[49] = 0; // unit unknown: only an aspect ratio, no resolution
        Assert.Null(StampLibrary.ReadDpi(png));
        // Other files have none, also a JPEG that names one
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, .. "JFIF\0"u8, 1, 1, 1, 0, 150, 0, 150, 0, 0];
        Assert.Null(StampLibrary.ReadDpi(jpeg));
        Assert.Null(StampLibrary.ReadDpi([1, 2, 3]));
    }

    [Fact]
    public void ConfiguredFolderWinsOverTheDefault()
    {
        var settings = new AppSettings();
        Assert.Equal(StampLibrary.DefaultFolder, StampLibrary.ResolveFolder(settings));
        Assert.EndsWith(Path.Combine(AppInfo.Name, "Stamps"), StampLibrary.DefaultFolder);
        settings.StampFolder = _dir;
        Assert.Equal(_dir, StampLibrary.ResolveFolder(settings));
    }

    [Fact]
    public void StampFolderSurvivesSaveAndLoad()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        new AppSettings { StampFolder = "/some/stamps" }.Save(path);
        Assert.Equal("/some/stamps", AppSettings.Load(path).StampFolder);
    }

    [Fact]
    public void AFolderThatIsGoneStartsOverWithAllSamples()
    {
        var samples = Path.Combine(_dir, "samples");
        Directory.CreateDirectory(Path.Combine(samples, "Numbers"));
        File.WriteAllBytes(Path.Combine(samples, "a.png"), [1]);
        File.WriteAllBytes(Path.Combine(samples, "Numbers", "01.png"), [1]);
        var folder = Path.Combine(_dir, "stamps");
        var provided = new List<string> { "stamp:a.png", "stamp:Numbers/01.png" }; // settings kept from before, the folder was deleted by hand

        Assert.False(StampLibrary.ProvideSamples(folder, samples, provided));
        Assert.True(File.Exists(Path.Combine(folder, "a.png")));
        Assert.True(File.Exists(Path.Combine(folder, "Numbers", "01.png")));
        Assert.Equal(2, provided.Count);

        // From now on a deleted sample stays deleted again
        File.Delete(Path.Combine(folder, "a.png"));
        Assert.False(StampLibrary.ProvideSamples(folder, samples, provided));
        Assert.False(File.Exists(Path.Combine(folder, "a.png")));
    }
}
