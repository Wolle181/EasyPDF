using System;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EasyPDF.App.Styles;

/// <summary>
/// Logo for the brand header when the program icon is made from a logo file: tools/icon/make_icon.py then writes
/// branding/&lt;brand&gt;/icons/header-logo.png (and deletes it again for the drawn icon), which the build embeds as
/// Assets/BrandLogo.png. Without it the header shows the mark drawn in XAML.
/// </summary>
public static class BrandLogo
{
    public static Uri Uri { get; } = new($"avares://{typeof(BrandLogo).Assembly.GetName().Name}/Assets/BrandLogo.png");

    private static readonly Lazy<Bitmap?> _image = new(() => Load(Uri));

    /// <summary>The logo, or null when the icon is the drawn mark.</summary>
    public static Bitmap? Image => _image.Value;

    /// <summary>Company logo of the brand for Help &gt; About (branding/&lt;brand&gt;/icons/company-logo.svg), or null without one.</summary>
    public static Uri CompanyUri { get; } = new($"avares://{typeof(BrandLogo).Assembly.GetName().Name}/Assets/CompanyLogo.svg");

    private static readonly Lazy<DrawingImage?> _company = new(() => AssetLoader.Exists(CompanyUri) ? SimpleSvg.Load(AssetLoader.Open(CompanyUri)) : null);

    /// <summary>Drawn as vector graphics: sharp at every size and display scaling.</summary>
    public static DrawingImage? Company => _company.Value;

    public static Bitmap? Load(Uri uri) => AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
}
