using System.Collections.Generic;
using System.IO;
using System.Linq;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Templates;
using Xunit;

namespace EasyPDF.Core.Tests;

public class TemplateLibraryTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fpc-templates-" + System.Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static TextTemplate T(string name, string text) => new() { Name = name, Text = text };

    /// <summary>Writes templates into the folder the way the Templates dialog does.</summary>
    private void Write(params TextTemplate[] templates) => Assert.True(TemplateLibrary.Save(_dir, [], templates));

    private string[] FileNames() => [.. Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).Order(System.StringComparer.Ordinal)];

    [Fact]
    public void SamplesArriveOnceAlsoInAFolderThatExistsAlready()
    {
        Write(T("Mine", "m"), T("Date", "my own date"));
        var provided = new List<string>();
        (string, TextTemplate)[] samples = [("Date", T("Date", "{$DATE}")), ("Page", T("Page x/y", "Page {$PAGE}\nof {$PAGES}"))];

        Assert.True(TemplateLibrary.ProvideSamples(_dir, samples, provided));
        Assert.Equal(["Date.txt", "Mine.txt", "Page x_y.txt"], FileNames());
        Assert.Equal(["template:Date", "template:Page"], provided);
        var loaded = TemplateLibrary.Load(_dir);
        Assert.Equal("my own date", loaded[0].Text); // a file of the same name is kept
        Assert.Equal("Page {$PAGE}\nof {$PAGES}", loaded[2].Text);
        Assert.Equal(Path.Combine(_dir, "Page x_y.txt"), loaded[2].FilePath);

        // Samples the user deleted don't come back, also not under another name (other language)
        File.Delete(Path.Combine(_dir, "Page x_y.txt"));
        Assert.False(TemplateLibrary.ProvideSamples(_dir, [("Page", T("Seite x von y", "Seite {$PAGE}"))], provided));
        Assert.Equal(["Date.txt", "Mine.txt"], FileNames());
    }

    [Fact]
    public void LoadReadsOnlyTextFilesSortedByNameWithoutTheFinalLineBreak()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "second\r\nline\r\n");
        File.WriteAllText(Path.Combine(_dir, "A.TXT"), "first");
        File.WriteAllText(Path.Combine(_dir, "notes.md"), "no template");
        var loaded = TemplateLibrary.Load(_dir);
        Assert.Equal(["A", "b"], loaded.Select(t => t.Name).ToArray());
        Assert.Equal("second\nline", loaded[1].Text);
        Assert.Empty(TemplateLibrary.Load(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void SaveWritesRenamesAndDeletesFiles()
    {
        Write(T("Keep", "k"), T("Old name", "o"), T("Gone", "g"));
        var before = TemplateLibrary.Load(_dir);
        var keep = before.Single(t => t.Name == "Keep");
        var renamed = before.Single(t => t.Name == "Old name");
        keep.Text = "k2";
        renamed.Name = "New name";
        List<TextTemplate> after = [keep, renamed, T("Added", "a\nb"), T("Added", "twice"), T("a/b:c?", "odd")];

        Assert.True(TemplateLibrary.Save(_dir, before, after));
        Assert.Equal(["Added (2).txt", "Added.txt", "Keep.txt", "New name.txt", "a_b_c_.txt"], FileNames());
        var loaded = TemplateLibrary.Load(_dir).ToDictionary(t => t.Name, t => t.Text);
        Assert.Equal("k2", loaded["Keep"]);
        Assert.Equal("o", loaded["New name"]);
        Assert.Equal("a\nb", loaded["Added"]);
        Assert.Equal("twice", loaded["Added (2)"]);
    }

    [Fact]
    public void ARenameThatOnlyChangesTheCaseKeepsTheTemplate()
    {
        Write(T("draft", "d"));
        var before = TemplateLibrary.Load(_dir);
        List<TextTemplate> after = [new() { Name = "Draft", Text = "d", FilePath = before[0].FilePath }];
        Assert.True(TemplateLibrary.Save(_dir, before, after));
        Assert.Equal(["Draft.txt"], FileNames());
    }

    [Fact]
    public void ConfiguredFolderWinsOverTheDefaultAndSurvivesSaveAndLoad()
    {
        var settings = new AppSettings();
        Assert.Equal(TemplateLibrary.DefaultFolder, TemplateLibrary.ResolveFolder(settings));
        Assert.EndsWith(Path.Combine(AppInfo.Name, "Templates"), TemplateLibrary.DefaultFolder);
        settings.TemplateFolder = _dir;
        Assert.Equal(_dir, TemplateLibrary.ResolveFolder(settings));

        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        settings.Save(path);
        Assert.Equal(_dir, AppSettings.Load(path).TemplateFolder);
    }

    [Fact]
    public void AFolderThatIsGoneStartsOverWithAllSamples()
    {
        var folder = Path.Combine(_dir, "gone");
        var provided = new List<string> { "template:Date" }; // settings kept from before, the folder was deleted by hand
        Assert.False(TemplateLibrary.ProvideSamples(folder, [("Date", T("Date", "{$DATE}"))], provided));
        Assert.True(File.Exists(Path.Combine(folder, "Date.txt")));
        Assert.Equal(["template:Date"], provided);
    }
}
