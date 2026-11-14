using System;
using System.IO;
using System.Linq;
using EasyPDF.Core.Stamps;
using Xunit;

namespace EasyPDF.Core.Tests;

public class StampImageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("easypdf-stampimage").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(255, 255, 255, 255, 0)]   // white
    [InlineData(252, 251, 253, 255, 0)]   // white with the noise of a scan
    [InlineData(232, 232, 232, 255, 131)] // light grey fades
    [InlineData(100, 200, 255, 255, 255)] // a colour stays: its darkest channel decides
    [InlineData(0, 0, 0, 255, 255)]       // ink
    [InlineData(232, 232, 232, 50, 50)]   // never more opaque than it was
    public void WhiteBecomesTransparentAndInkStays(byte b, byte g, byte r, byte a, byte expected)
    {
        var picture = new[] { b, g, r, a };

        var result = StampImage.WhiteToTransparent(picture);

        Assert.Equal(expected, result[3]);
        Assert.Equal(new[] { b, g, r }, result[..3]);
        Assert.Equal(a, picture[3]); // a copy
    }

    [Fact]
    public void SaveStampWritesThePngUnderACleanName()
    {
        var path = StampLibrary.SaveStamp(_dir, "a/b: c.", [1, 2]);

        Assert.Equal(Path.Combine(_dir, "a_b_ c.png"), path);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_dir)); // no temporary file stays
    }

    [Fact]
    public void SaveStampKeepsAnotherFileOfTheSameName()
    {
        File.WriteAllBytes(Path.Combine(_dir, "Stamp.png"), [1]);

        var path = StampLibrary.SaveStamp(_dir, "Stamp", [2]);

        Assert.Equal(Path.Combine(_dir, "Stamp (2).png"), path);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(_dir, "Stamp.png")));
    }

    [Fact]
    public void SaveStampReplacesItsOwnFileAlsoWhenRenamed()
    {
        var old = Path.Combine(_dir, "Stamp.png");
        File.WriteAllBytes(old, [1]);

        Assert.Equal(old, StampLibrary.SaveStamp(_dir, "Stamp", [2], old));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(old));

        var renamed = StampLibrary.SaveStamp(_dir, "Other", [3], old);
        Assert.Equal("Other.png", Path.GetFileName(Assert.Single(Directory.GetFiles(_dir))));
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(renamed));

        // Only the case changes
        var cased = StampLibrary.SaveStamp(_dir, "OTHER", [4], renamed);
        Assert.Equal("OTHER.png", Path.GetFileName(Assert.Single(Directory.GetFiles(_dir))));
        Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(cased));
    }

    [Fact]
    public void SaveStampCreatesTheFolderAndLeavesTheOldFileWhenItFails()
    {
        var folder = Path.Combine(_dir, "new", "folder");
        Assert.True(File.Exists(StampLibrary.SaveStamp(folder, "S", [1])));

        var blocked = Path.Combine(_dir, "file-not-folder");
        File.WriteAllBytes(blocked, [1]);
        Assert.ThrowsAny<IOException>(() => StampLibrary.SaveStamp(blocked, "S", [1]));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(blocked));
    }
}
