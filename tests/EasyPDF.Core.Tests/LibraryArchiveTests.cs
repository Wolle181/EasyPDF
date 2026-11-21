using System.IO;
using System.IO.Compression;
using System.Linq;
using EasyPDF.Core.Stamps;
using Xunit;

namespace EasyPDF.Core.Tests;

public class LibraryArchiveTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fpc-library-" + System.Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string Folder(string name) => Path.Combine(_dir, name);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void StampsWithSubfoldersAndTemplatesComeBackAsTheyWere()
    {
        Write(Path.Combine(Folder("stamps"), "Signature.png"), "sig");
        Write(Path.Combine(Folder("stamps"), "Numbers", "01.png"), "one");
        Write(Path.Combine(Folder("stamps"), "notes.txt"), "not a stamp");
        Write(Path.Combine(Folder("templates"), "Greeting.txt"), "Hello\nWorld");

        var zip = LibraryArchive.Pack(Folder("stamps"), Folder("templates"));

        Assert.Equal(2, LibraryArchive.Extract(zip, LibraryArchive.Stamps, Folder("stamps2"), ".png", subfolders: true));
        Assert.Equal(1, LibraryArchive.Extract(zip, LibraryArchive.Templates, Folder("templates2"), ".txt", subfolders: false));
        Assert.Equal("sig", File.ReadAllText(Path.Combine(Folder("stamps2"), "Signature.png")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(Folder("stamps2"), "Numbers", "01.png")));
        Assert.False(File.Exists(Path.Combine(Folder("stamps2"), "notes.txt")));
        Assert.Equal("Hello\nWorld", File.ReadAllText(Path.Combine(Folder("templates2"), "Greeting.txt")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void EmptyFoldersComeBackAsEmptyFolders()
    {
        // An empty folder is not a missing one: the samples the user deleted must not come back
        Directory.CreateDirectory(Folder("stamps"));
        var zip = LibraryArchive.Pack(Folder("stamps"), Folder("templates"));
        Assert.Equal(0, LibraryArchive.Extract(zip, LibraryArchive.Stamps, Folder("stamps2"), ".png", subfolders: true));
        Assert.Equal(0, LibraryArchive.Extract(zip, LibraryArchive.Templates, Folder("templates2"), ".txt", subfolders: false));
        Assert.True(Directory.Exists(Folder("stamps2")));
        Assert.True(Directory.Exists(Folder("templates2")));
    }

    [Fact]
    public void LeftOutFilesAreNotPacked()
    {
        Write(Path.Combine(Folder("stamps"), "Mine.png"), "m");
        Write(Path.Combine(Folder("stamps"), "Shared", "Theirs.png"), "t");
        var zip = LibraryArchive.Pack(Folder("stamps"), Folder("templates"), file => !file.Contains("Shared"));
        Assert.Equal(1, LibraryArchive.Extract(zip, LibraryArchive.Stamps, Folder("out"), ".png", subfolders: true));
        Assert.False(Directory.Exists(Path.Combine(Folder("out"), "Shared")));
    }

    [Fact]
    public void NamesThatLeaveTheFolderOrGoDeeperAreSkipped()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in new[] { "stamps/../evil.png", "stamps/a/b/deep.png", "stamps/../", "stamps/ok.png", "stamps//x.png", "stamps/a\\b.png", "other/ok.png" })
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open()))
                    writer.Write("x");
        var bytes = zip.ToArray();

        Assert.Equal(1, LibraryArchive.Extract(bytes, LibraryArchive.Stamps, Folder("out"), ".png", subfolders: true));
        Assert.Equal(["ok.png"], Directory.EnumerateFileSystemEntries(Folder("out")).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.png")));
        Assert.Equal(["a"], LibraryArchive.Subfolders(bytes, LibraryArchive.Stamps));
    }

    [Fact]
    public void SomethingElseThanAZipIsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => LibraryArchive.Extract([1, 2, 3], LibraryArchive.Stamps, Folder("out"), ".png", true));
    }
}
