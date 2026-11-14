using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>
/// Brand profiles (branding/&lt;id&gt;/Branding.props): complete, with ids of their own, and whatever belongs to one brand
/// stays inside its folder, so the rest of the repository works for every brand.
/// </summary>
public class BrandingTests
{
    private static readonly string[] Required =
        ["AppDisplayName", "BrandPrimaryColor", "BrandAccentColor", "Company", "Copyright", "AppAuthor", "MsiUpgradeCode", "MacBundleId"];

    private static readonly string[] Icons =
        ["app.ico", "app.icns", "app-16.png", "app-24.png", "app-32.png", "app-48.png", "app-64.png", "app-128.png", "app-256.png", "app-512.png"];

    /// <summary>The original brand: its name is also the technical name of projects and namespaces, so it may appear anywhere.</summary>
    private const string TechnicalName = "EasyPDF";

    private sealed record Profile(string Id, string Folder, XDocument Props)
    {
        public string Value(string name) => Props.Descendants(name).SingleOrDefault()?.Value.Trim() ?? "";
    }

    private static List<Profile> Profiles() =>
        Directory.GetDirectories(Path.Combine(RepoPaths.Root, "branding"))
            .Where(d => File.Exists(Path.Combine(d, "Branding.props")))
            .Select(d => new Profile(Path.GetFileName(d), d, XDocument.Load(Path.Combine(d, "Branding.props"))))
            .ToList();

    [Fact]
    public void EveryBrandProfileIsCompleteWithIdsOfItsOwn()
    {
        var profiles = Profiles();
        Assert.Contains(profiles, p => p.Id == "easypdf" && p.Value("AppDisplayName") == TechnicalName);
        Assert.Contains(profiles, p => p.Id == RepoPaths.Brand);
        foreach (var p in profiles)
        {
            foreach (var name in Required)
                Assert.False(string.IsNullOrEmpty(p.Value(name)), $"{p.Id}: {name} missing");
            Assert.Matches("^#[0-9A-Fa-f]{6}$", p.Value("BrandPrimaryColor"));
            Assert.Matches("^#[0-9A-Fa-f]{6}$", p.Value("BrandAccentColor"));
            Assert.True(Guid.TryParse(p.Value("MsiUpgradeCode"), out _), $"{p.Id}: MsiUpgradeCode is no GUID");
            Assert.Matches("^[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)+$", p.Value("MacBundleId"));
            foreach (var icon in Icons)
                Assert.True(File.Exists(Path.Combine(p.Folder, "icons", icon)), $"{p.Id}: icons/{icon} missing (python tools/icon/make_icon.py --brand {p.Id})");
        }
        // Two brands can be installed side by side only with names and installer ids of their own
        foreach (var name in new[] { "AppDisplayName", "MsiUpgradeCode", "MacBundleId" })
            Assert.Equal(profiles.Count, profiles.Select(p => p.Value(name).ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void BrandNamesAndWordsStayInTheirProfile()
    {
        var root = RepoPaths.Root;
        // The repository may carry a brand's name: its URL (named only in Directory.Build.props, see RepositoryUrlTests) is no brand text
        var files = RepositoryFiles(root).Select(f => (Path: f, Text: ReadText(Path.Combine(root, f))))
            .Where(f => f.Text != null)
            .Select(f => f.Path == "Directory.Build.props" ? (f.Path, Text: Regex.Replace(f.Text!, "<RepositoryUrl>[^<]*</RepositoryUrl>", "")) : f)
            .ToList();
        var problems = new List<string>();
        var profiles = Profiles();
        foreach (var p in profiles)
        {
            var patterns = p.Value("BrandWords").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(w => new Regex($@"\b{Regex.Escape(w)}\b")).ToList();
            var name = p.Value("AppDisplayName");
            if (!string.Equals(name, TechnicalName, StringComparison.OrdinalIgnoreCase))
                patterns.Add(new Regex(Regex.Escape(name), RegexOptions.IgnoreCase));    // also the lower-case package name
            // Its own folder and those of the same company's other brands (e.g. colour variants)
            var own = profiles.Where(o => o.Value("Company") == p.Value("Company")).Select(o => "branding/" + o.Id + "/").ToList();
            foreach (var (path, text) in files.Where(f => !own.Any(o => f.Path.StartsWith(o, StringComparison.OrdinalIgnoreCase))))
                foreach (var pattern in patterns)
                    if (pattern.Match(text!) is { Success: true } m)
                        problems.Add($"{path}: \"{m.Value}\" belongs to branding/{p.Id}");
        }
        Assert.True(problems.Count == 0, "Write \"the app\", {App} or <App> instead:\n" + string.Join("\n", problems));
    }

    /// <summary>The files git knows or would add (tracked and untracked, not ignored), relative with '/'.</summary>
    internal static IEnumerable<string> RepositoryFiles(string root)
    {
        var git = Process.Start(new ProcessStartInfo("git", "ls-files -z --cached --others --exclude-standard")
        {
            WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8,
        })!;
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct();
    }

    /// <summary>The file as text, or null for a binary or deleted file.</summary>
    internal static string? ReadText(string file)
    {
        if (!File.Exists(file))
            return null;
        var bytes = File.ReadAllBytes(file);
        return Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0 ? null : Encoding.UTF8.GetString(bytes);
    }
}
