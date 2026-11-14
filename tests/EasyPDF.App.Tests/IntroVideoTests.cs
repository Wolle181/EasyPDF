using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Introduction video: videos/introduction.&lt;language&gt;.mp4 next to the program, played from the brand header.</summary>
public class IntroVideoTests
{
    [Fact]
    public void TheVideoInTheLanguageIsChosenElseEnglishElseAny()
    {
        var folder = Path.Combine(Path.GetTempPath(), "easypdf-introvideo-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(IntroVideo.Find(folder, "de"));                     // no folder
            Directory.CreateDirectory(folder);
            Assert.Null(IntroVideo.Find(folder, "de"));                     // empty folder
            File.WriteAllBytes(Path.Combine(folder, "introduction.fr.mp4"), []);
            Assert.EndsWith("introduction.fr.mp4", IntroVideo.Find(folder, "de"));
            File.WriteAllBytes(Path.Combine(folder, "introduction.en.mp4"), []);
            Assert.EndsWith("introduction.en.mp4", IntroVideo.Find(folder, "de"));
            File.WriteAllBytes(Path.Combine(folder, "introduction.de.mp4"), []);
            Assert.EndsWith("introduction.de.mp4", IntroVideo.Find(folder, "de"));
            Assert.EndsWith("introduction.en.mp4", IntroVideo.Find(folder, "en"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public void TheHeaderButtonPlaysTheVideoAndShowsOnlyWithAVideo()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        var button = window.FindControl<RibbonButton>("VideoButton")!;
        Assert.Same(window.ViewModel.ShowIntroVideoCommand, button.Command);
        // The brand's videos are copied next to the program, also into the test output
        var brandHasVideos = Directory.Exists(Path.Combine(RepoPaths.BrandDir, "videos"))
                             && Directory.EnumerateFiles(Path.Combine(RepoPaths.BrandDir, "videos"), "introduction.*.mp4").Any();
        Assert.Equal(brandHasVideos, IntroVideo.IsAvailable);
        Assert.Equal(IntroVideo.IsAvailable, button.IsVisible);
        var help = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Last().Menu!;
        Assert.Equal(IntroVideo.IsAvailable, help.Items.OfType<NativeMenuItem>().Any(i => i.Command == window.ViewModel.ShowIntroVideoCommand));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheCommandAsksTheWindowToPlayTheVideo()
    {
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new AppSettings(), dialogs);
        await vm.ShowIntroVideoCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.IntroVideoShown);
    }
}
