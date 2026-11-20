using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using EasyPDF.App.Styles;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>The colour scheme: two brand colours in the brand profile, every other colour a shade of them.</summary>
public class BrandColorTests
{
    [Fact]
    public void BrandColorsComeFromTheBrandProfile()
    {
        Assert.Equal(Color.Parse(RepoPaths.BrandProp("BrandPrimaryColor")), BrandColors.Primary);
        Assert.Equal(Color.Parse(RepoPaths.BrandProp("BrandAccentColor")), BrandColors.Accent);
        // Optional: without it the header starts in a lighter primary
        var header = RepoPaths.BrandProp("BrandHeaderColor");
        Assert.Equal(header != "" ? Color.Parse(header) : BrandColors.Shade(BrandBase.Primary, 0.81, lighten: 0.07), BrandColors.Header);
    }

    [Fact]
    public void ShadesKeepTheHueOfTheirBrandColor()
    {
        var primary = Color.FromRgb(0x00, 0x1B, 0x41);
        Assert.Equal(primary, BrandColors.Shade(primary));
        AssertNear(Color.FromRgb(0x2F, 0x5C, 0x9E), BrandColors.Shade(primary, 0.54, 0.40));      // dark theme selection
        AssertNear(Color.FromRgb(0x0E, 0x2A, 0x55), BrandColors.Shade(primary, 0.72, lighten: 0.07)); // band hover
        AssertNear(Color.FromRgb(0xE1, 0xE5, 0xEB), BrandColors.Shade(primary, 0.20, 0.90));      // a cool grey

        // Another brand colour takes all shades along: same hue, the lightness of the role
        var green = Color.FromRgb(0x0B, 0x4D, 0x2C);
        var selection = BrandColors.Shade(green, 0.54, 0.40).ToHsl();
        Assert.InRange(selection.H, green.ToHsl().H - 2, green.ToHsl().H + 2);
        Assert.InRange(selection.L, 0.39, 0.41);
        Assert.Equal(0.25, BrandColors.Shade(Color.FromRgb(0x40, 0x40, 0x40), lightness: 0.5, lighten: -0.25).ToHsl().L, 2);
    }

    [AvaloniaFact]
    public void ThemeColorsAreShadesOfTheBrandColors()
    {
        var app = Application.Current!;
        Color Brush(string key, ThemeVariant theme) =>
            app.TryGetResource(key, theme, out var v) && v is ISolidColorBrush b ? b.Color : throw new InvalidOperationException(key);

        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Assert.Equal(BrandColors.Primary, Brush("Band.Background", theme));
            Assert.Equal(BrandColors.Accent, Brush("Primary.Background", theme));
            Assert.Equal(BrandColors.Primary, Brush("Primary.Foreground", theme));
        }
        Assert.Equal(BrandColors.Primary, Brush("Selection.Background", ThemeVariant.Light));
        Assert.Equal(BrandColors.Shade(BrandBase.Primary, 0.54, 0.40), Brush("Selection.Background", ThemeVariant.Dark));
        Assert.Equal(BrandColors.Accent, Brush("Brand.Accent", ThemeVariant.Light));
    }

    [Fact]
    public void StylesUseNoFixedColors()
    {
        // Only white and the red of the delete icons are fixed; everything else is {st:BrandColor ...}
        var allowed = new[] { "FFFFFF", "C8312F", "F06A6A" };
        var src = Path.Combine(RepoPaths.Root, "src");
        foreach (var file in Directory.GetFiles(src, "*.axaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var fixedColors = Regex.Matches(File.ReadAllText(file), @"#(?:[0-9A-Fa-f]{2})?([0-9A-Fa-f]{6})\b")
                .Select(m => m.Groups[1].Value.ToUpperInvariant()).Where(c => !allowed.Contains(c)).Distinct().ToList();
            Assert.True(fixedColors.Count == 0, $"{file} has fixed colours: {string.Join(", ", fixedColors)}");
        }
    }

    private static void AssertNear(Color expected, Color actual) =>
        Assert.True(Math.Abs(expected.R - actual.R) <= 4 && Math.Abs(expected.G - actual.G) <= 4 && Math.Abs(expected.B - actual.B) <= 4,
                    $"Expected about {expected}, got {actual}");
}
