using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EasyPDF.App.Styles;
using EasyPDF.App.Views;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Brand header: the mark drawn in XAML, or the logo when the program icon is made from a logo file.</summary>
public class BrandLogoTests
{
    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task HeaderShowsTheLogoOnlyWhenTheIconIsMadeFromOne()
    {
        // make_icon.py writes icons/header-logo.png (embedded as Assets/BrandLogo.png) only for a logo file (icons/logo.png)
        var hasLogo = File.Exists(Path.Combine(RepoPaths.BrandDir, "icons", "header-logo.png"));
        Assert.Equal(File.Exists(Path.Combine(RepoPaths.BrandDir, "icons", "logo.png")), hasLogo);
        Assert.Equal(hasLogo, BrandLogo.Image != null);

        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        Assert.Equal(!hasLogo, window.FindControl<Border>("BrandMark")!.IsVisible);
        Assert.Equal(hasLogo, window.FindControl<Image>("BrandLogoImage")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LogoReplacesTheDrawnMark()
    {
        var uri = new Uri(BrandLogo.Uri.ToString().Replace("BrandLogo.png", "AppIcon-128.png"));
        var logo = BrandLogo.Load(uri);
        Assert.NotNull(logo);
        Assert.Null(BrandLogo.Load(new Uri(BrandLogo.Uri.ToString().Replace("BrandLogo.png", "Missing.png"))));

        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        var mark = window.FindControl<Border>("BrandMark")!;
        var image = window.FindControl<Image>("BrandLogoImage")!;
        window.ShowBrandLogo(logo);
        await Pump();
        Assert.False(mark.IsVisible);
        Assert.True(image.IsVisible);
        Assert.Same(logo, image.Source);
        Assert.Equal(42, image.Bounds.Width);                  // same place and size as the mark

        window.ShowBrandLogo(null);
        await Pump();
        Assert.True(mark.IsVisible);
        Assert.False(image.IsVisible);
        window.Close();
    }
}
