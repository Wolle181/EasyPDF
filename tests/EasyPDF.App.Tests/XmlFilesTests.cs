using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>
/// Every XML file of the repository (projects, publish profiles, WiX, resources, XAML, plists) must be well-formed.
/// Some are only loaded on one platform – the macOS publish profiles, the WiX project on Windows – so a broken one
/// would otherwise only show up there (an XML comment with "--" once broke the macOS AOT build on CI).
/// </summary>
public class XmlFilesTests
{
    private static readonly string[] Extensions =
        [".csproj", ".wixproj", ".pubxml", ".props", ".targets", ".wxs", ".wxl", ".resx", ".axaml", ".xaml", ".slnx", ".plist", ".xml", ".svg", ".manifest"];

    [Fact]
    public void AllXmlFilesAreWellFormed()
    {
        var root = RepoPaths.Root;
        var files = BrandingTests.RepositoryFiles(root)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => File.Exists(Path.Combine(root, f)))
            .ToList();
        Assert.Contains(files, f => f.EndsWith(".pubxml", StringComparison.Ordinal));

        var problems = files
            .Select(f =>
            {
                try
                {
                    XDocument.Load(Path.Combine(root, f));
                    return null;
                }
                catch (XmlException ex)
                {
                    return $"{f}: {ex.Message}";
                }
            })
            .OfType<string>()
            .ToList();
        Assert.True(problems.Count == 0, "Not well-formed XML:\n" + string.Join("\n", problems));
    }
}
