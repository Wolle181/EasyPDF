using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EasyPDF.App.Services;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>
/// The git repository is named in one place, <c>RepositoryUrl</c> in Directory.Build.props, so moving the project
/// changes one line: the app, the installers and the docs take it from there or link relative to GitHub.
/// </summary>
public class RepositoryUrlTests
{
    private static string RepositoryUrl() =>
        XDocument.Load(Path.Combine(RepoPaths.Root, "Directory.Build.props")).Descendants("RepositoryUrl").Single().Value.Trim();

    [Fact]
    public void AboutShowsTheRepositoryOfDirectoryBuildProps()
    {
        Assert.StartsWith("https://github.com/", RepositoryUrl());
        Assert.Equal(RepositoryUrl(), VersionInfo.Repository);
    }

    [Fact]
    public void NoOtherFileNamesARepositoryOfTheOwner()
    {
        // Any https://github.com/<owner>/... link, also to an old name of the repository
        var owner = new Uri(RepositoryUrl()).AbsolutePath.Trim('/').Split('/')[0];
        var pattern = new Regex($@"github\.com/{Regex.Escape(owner)}/", RegexOptions.IgnoreCase);
        var root = RepoPaths.Root;
        var problems = BrandingTests.RepositoryFiles(root)
            .Where(f => f != "Directory.Build.props")
            .Where(f => BrandingTests.ReadText(Path.Combine(root, f)) is { } text && pattern.IsMatch(text))
            .ToList();
        Assert.True(problems.Count == 0,
            "Take RepositoryUrl from Directory.Build.props (or link relative, e.g. ../../releases in a README):\n" + string.Join("\n", problems));
    }
}
