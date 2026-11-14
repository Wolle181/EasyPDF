using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia.Media;

namespace EasyPDF.App.Styles;

/// <summary>The two brand colours every colour of the app is derived from (App.axaml, Styles/*.axaml).</summary>
public enum BrandBase
{
    /// <summary>Dark main colour: band, selection, text on the accent colour, the tint of the greys.</summary>
    Primary,
    /// <summary>Light signal colour: line under the band, Save button, icon accents.</summary>
    Accent,
    /// <summary>Top of the brand header's gradient (it ends in the primary colour of the band below).</summary>
    Header,
}

/// <summary>
/// Brand colours, set once as <c>BrandPrimaryColor</c> and <c>BrandAccentColor</c> in the brand profile (branding/&lt;brand&gt;/Branding.props) (they reach the
/// app as assembly metadata), optionally <c>BrandHeaderColor</c>. All other colours are shades of them: same hue, a share of their saturation and a lightness
/// of their own, so a new pair of brand colours recolours the whole app, light and dark.
/// </summary>
public static class BrandColors
{
    public static Color Primary { get; } = FromMetadata("BrandPrimaryColor", Color.FromRgb(0x00, 0x3A, 0x66));

    public static Color Accent { get; } = FromMetadata("BrandAccentColor", Color.FromRgb(0x79, 0xA5, 0xD7));

    /// <summary>
    /// Top of the header gradient: <c>BrandHeaderColor</c> if the brand profile sets it, else a slightly lighter and greyer
    /// primary colour.
    /// </summary>
    public static Color Header { get; } = FromMetadata("BrandHeaderColor", Shade(Primary, 0.81, lighten: 0.07));

    public static Color Of(BrandBase brand) => brand switch
    {
        BrandBase.Accent => Accent,
        BrandBase.Header => Header,
        _ => Primary,
    };

    /// <summary>
    /// A shade of <paramref name="brand"/>: its hue, <paramref name="saturation"/> times its saturation, and the lightness
    /// <paramref name="lightness"/> (0 black … 1 white; NaN = the brand colour's own) plus <paramref name="lighten"/>.
    /// </summary>
    public static Color Shade(BrandBase brand, double saturation = 1, double lightness = double.NaN, double lighten = 0) =>
        Shade(Of(brand), saturation, lightness, lighten);

    /// <summary>The same shade of any colour (tests, other brand colours).</summary>
    public static Color Shade(Color color, double saturation = 1, double lightness = double.NaN, double lighten = 0)
    {
        var hsl = color.ToHsl();
        var l = (double.IsNaN(lightness) ? hsl.L : lightness) + lighten;
        return new HslColor(1, hsl.H, Math.Clamp(hsl.S * saturation, 0, 1), Math.Clamp(l, 0, 1)).ToRgb();
    }

    private static Color FromMetadata(string key, Color fallback)
    {
        var value = typeof(BrandColors).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
        return value != null && Color.TryParse(value.Trim(), out var color) ? color : fallback;
    }
}

/// <summary>
/// XAML markup extension for a brand shade: <c>Color="{st:BrandColor Primary, S=0.21, L=0.72}"</c>. S = share of the
/// brand colour's saturation (default 1), L = lightness 0 … 1 (default: the brand colour's), Lighten = added to the
/// lightness (negative = darker; for shades that must stay next to the brand colour, such as hover).
/// </summary>
public sealed class BrandColorExtension(BrandBase brand)
{
    public BrandBase Brand { get; set; } = brand;

    public double S { get; set; } = 1;

    public double L { get; set; } = double.NaN;

    public double Lighten { get; set; }

    /// <summary>Opacity 0 … 1 (default opaque).</summary>
    public double Alpha { get; set; } = 1;

    public Color ProvideValue(IServiceProvider serviceProvider)
    {
        var c = BrandColors.Shade(Brand, S, L, Lighten);
        return Color.FromArgb((byte)Math.Round(Math.Clamp(Alpha, 0, 1) * 255), c.R, c.G, c.B);
    }
}

/// <summary>
/// XAML markup extension for a shadow in a brand shade: <c>BoxShadow="{st:BrandShadow '0 1 2 0', Alpha=0.13}"</c>
/// (offset x, offset y, blur, spread as in Avalonia's BoxShadow; colour as in <see cref="BrandColorExtension"/>).
/// </summary>
public sealed class BrandShadowExtension(string geometry)
{
    public string Geometry { get; set; } = geometry;

    public BrandBase Brand { get; set; } = BrandBase.Primary;

    public double Alpha { get; set; } = 1;

    public BoxShadows ProvideValue(IServiceProvider serviceProvider)
    {
        var c = new BrandColorExtension(Brand) { Alpha = Alpha }.ProvideValue(serviceProvider);
        return BoxShadows.Parse(string.Create(CultureInfo.InvariantCulture, $"{Geometry} #{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"));
    }
}
