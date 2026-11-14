using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EasyPDF.App.Localization;
using Xunit;

namespace EasyPDF.App.Tests;

public class LocalizationTests
{
    private static string ResourceDir => Path.Combine(RepoPaths.Root, "src", "EasyPDF.App", "Resources");

    private static System.Collections.Generic.Dictionary<string, string> Read(string file) =>
        XDocument.Load(Path.Combine(ResourceDir, file)).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);

    [Fact]
    public void EveryEnglishKeyExistsInGermanAndViceVersa()
    {
        var en = Read("Strings.resx");
        var de = Read("Strings.de.resx");
        Assert.Empty(en.Keys.Except(de.Keys));
        Assert.Empty(de.Keys.Except(en.Keys));
        Assert.All(de, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
    }

    [Fact]
    public void GermanTextsKeepTheSamePlaceholders()
    {
        var en = Read("Strings.resx");
        var de = Read("Strings.de.resx");
        foreach (var (key, value) in en)
        {
            var a = Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x);
            var b = Regex.Matches(de[key], @"\{\d+\}").Select(m => m.Value).OrderBy(x => x);
            Assert.True(a.SequenceEqual(b), $"Placeholders differ for {key}");
        }
    }

    [Fact]
    public void EveryKeyUsedInXamlOrCodeExists()
    {
        var keys = Read("Strings.resx").Keys.ToHashSet();
        var src = Path.Combine(RepoPaths.Root, "src", "EasyPDF.App");
        var used = new System.Collections.Generic.HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".axaml") || f.EndsWith(".cs")) && !f.EndsWith("Loc.cs") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"\{loc:Tr (\w+)\}"))
                used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"Loc\.[TF]\(""(\w+)"""))
                used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"new Entry\(""(\w+)"""))
                used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"\(""(Info\w+)"","))
                used.Add(m.Groups[1].Value);
        }
        Assert.NotEmpty(used);
        Assert.Empty(used.Except(keys));
    }

    [Theory]
    [InlineData("en", "1 page", "2 pages", "1 file", "2 files", "1 word", "2 words", "1 character", "2 characters")]
    [InlineData("de", "1 Seite", "2 Seiten", "1 Datei", "2 Dateien", "1 Wort", "2 Wörter", "1 Zeichen", "2 Zeichen")]
    public void CountsUseTheSingularForOne(string language, string page, string pages, string file, string files, string word, string words, string character, string characters)
    {
        var old = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(language);
        try
        {
            Assert.Equal(page, Loc.Pages(1));
            Assert.Equal(pages, Loc.Pages(2));
            Assert.Equal(file, Loc.Files(1));
            Assert.Equal(files, Loc.Files(2));
            Assert.Equal(word, Loc.Words(1));
            Assert.Equal(words, Loc.Words(2));
            Assert.Equal(character, Loc.Characters(1));
            Assert.Equal(characters, Loc.Characters(2));
        }
        finally
        {
            CultureInfo.CurrentUICulture = old;
        }
    }

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.de.resx")]
    public void NoTextWritesACountBeforeAFixedPlural(string file)
    {
        // A count that can be 1 goes through Loc.Pages etc.; ConcatTotal lists at least two files, so at least two pages
        var wrong = Read(file).Where(kv => !kv.Key.StartsWith("Count") && kv.Key != "ConcatTotal"
                && Regex.IsMatch(kv.Value, @"\{\d+\} (pages|files|words|characters|Seiten|Dateien|Wörter|Zeichen)\b"))
            .Select(kv => kv.Key);
        Assert.Empty(wrong);
    }

    [Fact]
    public void ShownNameIsSetOnlyInTheBrandProfile()
    {
        var name = RepoPaths.BrandProp("AppDisplayName");
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Equal(name, Localization.Loc.AppName);
        Assert.Equal(name, Localization.Loc.T("AppName"));
        Assert.Equal($"About {name}", Localization.Loc.T("AboutTitle"));

        // Texts and installer sources say {App}, $(var.AppDisplayName) or @APPNAME@, never the name itself
        if (name == "EasyPDF")
            return;                                            // the technical name appears everywhere on purpose
        var installer = Path.Combine(RepoPaths.Root, "installer");
        var files = new[] { Path.Combine(ResourceDir, "Strings.resx"), Path.Combine(ResourceDir, "Strings.de.resx"),
                            Path.Combine(installer, "windows", "Package.wxs"), Path.Combine(installer, "macos", "Info.plist"),
                            Path.Combine(installer, "linux", "easypdf.desktop") }
            .Concat(Directory.GetFiles(Path.Combine(installer, "linux", "filemanager")));
        foreach (var file in files)
            Assert.False(File.ReadAllText(file).Contains(name), $"{file} names {name} directly");
    }
}
