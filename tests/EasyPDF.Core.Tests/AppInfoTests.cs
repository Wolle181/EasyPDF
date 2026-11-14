using System.IO;
using Xunit;

namespace EasyPDF.Core.Tests;

public class AppInfoTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fpc-appinfo-" + System.Guid.NewGuid().ToString("N"));

    public AppInfoTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ThePreferredFolderIsUsedWhenItCanBeWritten()
    {
        string preferred = Path.Combine(_dir, "documents", "App"), fallback = Path.Combine(_dir, "local", "App");
        Assert.Equal(preferred, AppInfo.ChooseUserFolder(preferred, fallback));
        Assert.True(Directory.Exists(preferred));
        Assert.Empty(Directory.GetFileSystemEntries(preferred)); // the check leaves nothing behind
        Assert.False(Directory.Exists(fallback));
    }

    [Fact]
    public void TheFallbackIsUsedWhenThePreferredFolderCantBeWrittenAndStaysInUse()
    {
        // A file where a folder would have to be: creating the folder fails like a blocked Documents folder
        var blocked = Path.Combine(_dir, "documents");
        File.WriteAllText(blocked, "");
        string preferred = Path.Combine(blocked, "App"), fallback = Path.Combine(_dir, "local", "App");
        Assert.Equal(fallback, AppInfo.ChooseUserFolder(preferred, fallback));
        Assert.True(Directory.Exists(fallback));

        // The block is lifted: the user's files are in the fallback, so it stays
        File.Delete(blocked);
        Assert.Equal(fallback, AppInfo.ChooseUserFolder(preferred, fallback));
        Assert.False(Directory.Exists(preferred));
    }

    [Fact]
    public void AnExistingPreferredFolderIsPreferredOverAnExistingFallback()
    {
        string preferred = Path.Combine(_dir, "documents", "App"), fallback = Path.Combine(_dir, "local", "App");
        Directory.CreateDirectory(preferred);
        Directory.CreateDirectory(fallback);
        Assert.Equal(preferred, AppInfo.ChooseUserFolder(preferred, fallback));
    }

    [Fact]
    public void ThePreferredFolderStaysWhenNothingCanBeWritten()
    {
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "");
        string preferred = Path.Combine(blocked, "a"), fallback = Path.Combine(blocked, "b");
        Assert.Equal(preferred, AppInfo.ChooseUserFolder(preferred, fallback));
    }
}
