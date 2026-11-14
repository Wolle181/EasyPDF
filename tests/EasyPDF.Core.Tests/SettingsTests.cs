using System.IO;
using EasyPDF.Core.Layout;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void UserFoldersAreNamedAfterTheShownName()
    {
        var dir = System.AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "EasyPDF.slnx")))
            dir = Path.GetDirectoryName(dir)!;
        // The brand profile the build uses (Brand in Directory.Build.props, passed in as assembly metadata)
        var brand = System.Linq.Enumerable.Single(System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(SettingsTests).Assembly), a => a.Key == "Brand").Value!;
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(dir, "branding", brand, "Branding.props"));
        var name = System.Linq.Enumerable.Single(props.Descendants("AppDisplayName")).Value;
        Assert.Equal(name, AppInfo.Name);
        Assert.Equal(name, Path.GetFileName(Path.GetDirectoryName(AppSettings.DefaultPath)));
        Assert.Equal(name, Path.GetFileName(AppInfo.TempFolder));
    }

    [Fact]
    public void RecentFilesAreMostRecentFirstDistinctAndLimited()
    {
        var s = new AppSettings();
        for (var i = 0; i < 15; i++)
            s.AddRecentFile(Path.GetFullPath($"file{i}.pdf"));
        Assert.Equal(AppSettings.MaxRecentFiles, s.RecentFiles.Count);
        Assert.EndsWith("file14.pdf", s.RecentFiles[0]);

        s.AddRecentFile("file10.pdf");
        Assert.EndsWith("file10.pdf", s.RecentFiles[0]);
        Assert.Equal(AppSettings.MaxRecentFiles, s.RecentFiles.Count);

        s.RemoveRecentFile(Path.GetFullPath("file10.pdf"));
        Assert.DoesNotContain(s.RecentFiles, p => p.EndsWith("file10.pdf"));
        s.ClearRecentFiles();
        Assert.Empty(s.RecentFiles);
    }

    [Fact]
    public void InstallerChoiceIsTakenOverOncePerInstallerRun()
    {
        var s = new AppSettings();
        Assert.False(s.ApplyInstallerChoice("de", "Dark", null));          // no stamp: dialog not shown (e.g. silent repair)
        Assert.False(s.ApplyInstallerChoice("de", "Dark", ""));
        Assert.Equal("", s.Language);

        Assert.True(s.ApplyInstallerChoice("de", "dark", "2026-10-08 10:00"));
        Assert.Equal("de", s.Language);
        Assert.Equal(ThemeSetting.Dark, s.Theme);

        s.Language = "en";                                                  // changed later in the app: sticks
        s.Theme = ThemeSetting.Light;
        Assert.False(s.ApplyInstallerChoice("de", "Dark", "2026-10-08 10:00"));
        Assert.Equal("en", s.Language);
        Assert.Equal(ThemeSetting.Light, s.Theme);

        Assert.True(s.ApplyInstallerChoice("system", "System", "2026-11-01 09:00"));   // next install
        Assert.Equal("", s.Language);
        Assert.Equal(ThemeSetting.System, s.Theme);

        Assert.True(s.ApplyInstallerChoice("fr", "Purple", "2026-12-01 09:00"));       // unknown values are ignored
        Assert.Equal("", s.Language);
        Assert.Equal(ThemeSetting.System, s.Theme);
    }

    [Fact]
    public void RoundTrip()
    {
        using var dir = new TempDir();
        var path = dir.File("sub/settings.json");
        var s = new AppSettings
        {
            Theme = ThemeSetting.Dark,
            Language = "de",
            ZoomMode = ZoomMode.Custom,
            CustomZoomPercent = 150,
            WindowWidth = 900,
            RibbonCollapsed = true,
        };
        s.AddRecentFile("a.pdf");
        s.Save(path);

        var loaded = AppSettings.Load(path);
        Assert.Equal(ThemeSetting.Dark, loaded.Theme);
        Assert.Equal("de", loaded.Language);
        Assert.Equal(ZoomMode.Custom, loaded.ZoomMode);
        Assert.Equal(150, loaded.CustomZoomPercent);
        Assert.Equal(900, loaded.WindowWidth);
        Assert.True(loaded.RibbonCollapsed);
        Assert.Single(loaded.RecentFiles);
    }

    [Fact]
    public void MissingOrBrokenFileGivesDefaults()
    {
        using var dir = new TempDir();
        Assert.Equal(ThemeSetting.System, AppSettings.Load(dir.File("none.json")).Theme);
        File.WriteAllText(dir.File("bad.json"), "{ not json");
        Assert.Equal(ThemeSetting.System, AppSettings.Load(dir.File("bad.json")).Theme);
    }

    /// <summary>Settings made in code (tests) never touch the user's settings file; loaded ones save back to their file.</summary>
    [Fact]
    public void SavingWithoutPathWritesToTheLoadedFileOnly()
    {
        var inMemory = new AppSettings { Theme = ThemeSetting.Dark };
        Assert.Null(inMemory.FilePath);
        inMemory.Save(); // nothing to write to: no exception, no file

        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var loaded = AppSettings.Load(path); // missing file: defaults, saved there later
        Assert.Equal(path, loaded.FilePath);
        loaded.Theme = ThemeSetting.Light;
        loaded.Save();
        Assert.Equal(ThemeSetting.Light, AppSettings.Load(path).Theme);
        Assert.DoesNotContain("FilePath", File.ReadAllText(path));
    }

    [Fact]
    public void TheMenuBarIsHiddenUnlessSwitchedOn()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("old.json"), "{ \"Theme\": \"Dark\" }"); // written before the option existed
        Assert.False(AppSettings.Load(dir.File("old.json")).ShowMenuBar);
        new AppSettings { ShowMenuBar = true }.Save(dir.File("on.json"));
        Assert.True(AppSettings.Load(dir.File("on.json")).ShowMenuBar);
    }
}
