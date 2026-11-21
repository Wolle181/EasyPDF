using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Downloads;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>What the shared user interface does differently in the web version: hidden commands, dialogs as overlays.</summary>
public class WebModeTests
{
    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static async Task Click(Window window, Visual target)
    {
        var p = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump(100);
    }

    [AvaloniaFact]
    public async Task TheWebVersionHidesWhatItDoesNotOffer()
    {
        PlatformFeatures.Current = PlatformFeatures.Web((vm, command) => command == vm.OpenCommand || command == vm.SaveCommand);
        try
        {
            var window = new MainWindow { Width = 1200, Height = 700 };
            window.Show();
            await Pump();

            var stamp = window.FindControl<RibbonButton>("StampButton")!;
            Assert.False(stamp.IsVisible);
            Assert.False(stamp.FindAncestorOfType<RibbonGroup>()!.IsVisible);       // the whole group, not only its buttons
            var open = window.GetLogicalDescendants().OfType<RibbonButton>().First(b => b.Command == window.ViewModel.OpenCommand);
            Assert.True(open.IsVisible);
            Assert.False(window.FindControl<RibbonButton>("SettingsButton")!.IsVisible);
            Assert.False(window.FindControl<NativeMenuBar>("MenuBar")!.IsVisible);
            Assert.Equal(Loc.T("BrandSubtitleWeb"), window.FindControl<TextBlock>("BrandSubtitle")!.Text);
            Assert.True(window.View.IsAvailable(window.ViewModel.SaveCommand));
            Assert.False(window.View.IsAvailable(window.ViewModel.PrintCommand));
            window.Close();
        }
        finally
        {
            PlatformFeatures.Current = PlatformFeatures.Desktop;
        }
    }

    [AvaloniaFact]
    public async Task TheDesktopOffersEverything()
    {
        var window = new MainWindow { Width = 1200, Height = 700 };
        window.Show();
        await Pump();
        Assert.True(window.FindControl<RibbonButton>("StampButton")!.IsVisible);
        Assert.True(window.View.IsAvailable(window.ViewModel.PrintCommand));
        Assert.Equal(Loc.T("BrandSubtitle"), window.FindControl<TextBlock>("BrandSubtitle")!.Text);
        window.Close();
    }

    [Fact]
    public void SplittingOnTheWebNeedsNoFolder()
    {
        PlatformFeatures.Current = PlatformFeatures.Web((_, _) => true);
        try
        {
            var vm = new EasyPDF.App.ViewModels.SplitDialogViewModel(new SplitDialogContext(10, [], "/does/not/exist", "doc"));
            vm.PagesPerPart = "4";
            Assert.False(vm.NeedsFolder);
            Assert.True(vm.IsValid);                       // the folder does not exist, but none is needed
            Assert.Equal(3, vm.BuildRequest()!.Parts.Count); // 4 + 4 + 2 pages
        }
        finally
        {
            PlatformFeatures.Current = PlatformFeatures.Desktop;
        }
    }

    [AvaloniaFact]
    public async Task OnTheWebSettingsOfferThemeAndLanguageOnly()
    {
        PlatformFeatures.Current = PlatformFeatures.Web((_, _) => true);
        try
        {
            var (window, layer) = LayerWindow();
            var shown = layer.ShowAsync(new SettingsView(new EasyPDF.Core.Settings.AppSettings(), () => { }));
            await Pump();
            var view = layer.GetVisualDescendants().OfType<SettingsView>().Single();
            Assert.False(view.FindControl<StackPanel>("StampSection")!.IsVisible);
            Assert.False(view.FindControl<StackPanel>("PdfFilesSection")!.IsVisible);
            Assert.Equal(2, view.GetVisualDescendants().OfType<ComboBox>().Count(c => c.IsEffectivelyVisible)); // theme, language
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Pump();
            Assert.True(shown.IsCompleted);
            window.Close();
        }
        finally
        {
            PlatformFeatures.Current = PlatformFeatures.Desktop;
        }
    }

    [AvaloniaFact]
    public async Task TheQuickGuideOnTheWebShowsWhatItOffersWithIcons()
    {
        PlatformFeatures.Current = PlatformFeatures.Web((vm, c) => c == vm.OpenCommand);
        try
        {
            var window = new MainWindow { Width = 1200, Height = 800 };
            window.Show();
            await Pump();
            // Inside the main view (as in the browser): the guide's icons are decoration, not commands to hide
            var layer = window.FindControl<DialogLayer>("Dialogs")!;
            _ = layer.ShowAsync(new QuickGuideView());
            await Pump();
            var rows = layer.GetVisualDescendants().OfType<Grid>().Where(g => g.Tag is string).Select(g => (string)g.Tag!).ToList();
            Assert.Equal(QuickGuideView.WebTopics.Select(t => t.Key), rows);
            Assert.DoesNotContain("Stamp", rows);
            Assert.Contains("Share", rows);
            Assert.DoesNotContain("Mail", rows);
            Assert.All(layer.GetVisualDescendants().OfType<RibbonButton>(), b => Assert.True(b.IsVisible));
            window.Close();
        }
        finally
        {
            PlatformFeatures.Current = PlatformFeatures.Desktop;
        }
    }

    [AvaloniaFact]
    public async Task OnTheWebTheEmailButtonIsShare()
    {
        PlatformFeatures.Current = PlatformFeatures.Web((vm, c) => c == vm.SendByMailCommand);
        try
        {
            var window = new MainWindow { Width = 1200, Height = 700 };
            window.Show();
            await Pump();
            var button = window.FindControl<RibbonButton>("SendByMailButton")!;
            Assert.True(button.IsVisible);
            Assert.Equal(Loc.T("BtnShare"), button.Label);
            Assert.Same(window.FindResource("Icon.Share"), button.Icon);
            window.Close();
        }
        finally
        {
            PlatformFeatures.Current = PlatformFeatures.Desktop;
        }
        var desktop = new MainWindow { Width = 1200, Height = 700 };
        desktop.Show();
        Assert.Equal(Loc.T("BtnSendByMail"), desktop.FindControl<RibbonButton>("SendByMailButton")!.Label);
        desktop.Close();
    }

    [AvaloniaFact]
    public async Task OnTheWebTheDesktopPackagesCanBeDownloaded()
    {
        var downloaded = new List<string>();
        var packages = new DesktopDownloads.WebPackages(
            [new("App-1.0.0.18-Windows-Installer.msi", 60_000_000, DesktopPackageKind.WindowsInstaller, "1.0.0.18"),
             new("App-1.0.0.18-Linux-x64.deb", 50_000_000, DesktopPackageKind.LinuxDeb, "1.0.0.18")],
            DesktopDownloads.KindsFor("Linux"), p => downloaded.Add(p.Name));
        PlatformFeatures.Current = PlatformFeatures.Web((vm, c) => c == vm.ShowDownloadsCommand);
        DesktopDownloads.Web = packages;
        try
        {
            var window = new MainWindow { Width = 1200, Height = 700 };
            window.Show();
            await Pump();
            var button = window.FindControl<RibbonButton>("DownloadButton")!;
            Assert.True(button.IsEffectivelyVisible);
            Assert.Same(window.ViewModel.ShowDownloadsCommand, button.Command);
            var dialogs = new FakeDialogs();
            await new MainViewModel(new EasyPDF.Core.Settings.AppSettings(), dialogs).ShowDownloadsCommand.ExecuteAsync(null);
            Assert.Same(packages, Assert.Single(dialogs.DownloadsShown));

            // In the browser the dialog is an overlay
            var layer = window.FindControl<DialogLayer>("Dialogs")!;
            _ = layer.ShowAsync(new DownloadsView(packages));
            await Pump();
            // The browser's system is preselected and shows only its packages
            Button? Download(DesktopPackageKind kind) =>
                layer.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == DownloadsView.ButtonName(kind));
            RadioButton System(DesktopSystem system) =>
                layer.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == DownloadsView.SystemButtonName(system));
            Assert.True(System(DesktopSystem.Linux).IsChecked);
            Assert.Equal(Loc.F("DownloadsSystemOwn", Loc.T("DownloadsSystemLinux")), System(DesktopSystem.Linux).Content);
            Assert.False(System(DesktopSystem.MacOS).IsEnabled);              // no package for it
            Assert.Null(Download(DesktopPackageKind.WindowsInstaller));
            var linux = Download(DesktopPackageKind.LinuxDeb)!;
            Assert.Contains("primary", linux.Classes);
            linux.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            // Any other system can be chosen
            var windows = System(DesktopSystem.Windows);
            var at = windows.TranslatePoint(new Point(windows.Bounds.Width / 2, windows.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            await Pump();
            Assert.True(windows.IsChecked);
            Assert.Null(Download(DesktopPackageKind.LinuxDeb));
            Download(DesktopPackageKind.WindowsInstaller)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(["App-1.0.0.18-Linux-x64.deb", "App-1.0.0.18-Windows-Installer.msi"], downloaded);
            window.Close();
        }
        finally
        {
            PlatformFeatures.Current = PlatformFeatures.Desktop;
            DesktopDownloads.Web = null;
        }
        // The desktop program offers none
        var desktop = new MainWindow { Width = 1200, Height = 700 };
        desktop.Show();
        Assert.False(desktop.FindControl<RibbonButton>("DownloadButton")!.IsVisible);
        desktop.Close();
    }

    [Theory]
    [InlineData("Windows", DesktopPackageKind.WindowsInstaller)]
    [InlineData("macOS", DesktopPackageKind.MacOS)]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0", DesktopPackageKind.LinuxTar)]
    public void TheBrowsersSystemPicksItsPackages(string platform, DesktopPackageKind kind) =>
        Assert.Contains(kind, DesktopDownloads.KindsFor(platform));

    [Fact]
    public void PhonesGetNoPackageMarked() => Assert.Empty(DesktopDownloads.KindsFor("Android"));

    [Fact]
    public void AboutListsTheWebAddressOfTheBrand()
    {
        var rows = VersionInfo.Collect();
        if (VersionInfo.WebAppUrl.Length > 0)
            Assert.Contains(rows, r => r.LabelKey == "InfoWebVersion" && r.Value == VersionInfo.WebAppUrl);
        else
            Assert.DoesNotContain(rows, r => r.LabelKey == "InfoWebVersion");
    }

    [AvaloniaFact]
    public void AboutShowsWhenTheServerWasDeployedAndWhetherInAContainer()
    {
        PlatformFeatures.Current = PlatformFeatures.Web((_, _) => true);
        VersionInfo.WebServer = new WebServerInfo(new DateTimeOffset(2026, 11, 21, 13, 5, 0, TimeSpan.Zero), InContainer: true);
        try
        {
            var deployed = new DateTimeOffset(2026, 11, 21, 13, 5, 0, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var view = new AboutView();
            var window = new Window { Content = view };
            window.Show();
            var row = Assert.Single(((IEnumerable<AboutRow>)view.FindControl<ItemsControl>("InfoList")!.ItemsSource!), r => r.Value == deployed);
            Assert.Equal(Loc.T("InfoDeployed"), row.Label);
            Assert.Equal(Loc.T("InfoHostingContainer"), row.Badge);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == row.Badge && t.IsEffectivelyVisible);
            Assert.Contains($"{Loc.T("InfoDeployed")}: {deployed} ({Loc.T("InfoHostingContainer")})", AboutView.BuildText());

            VersionInfo.WebServer = VersionInfo.WebServer with { InContainer = false };
            Assert.Equal(Loc.T("InfoHostingNative"), VersionInfo.HostingBadge);
            window.Close();
        }
        finally
        {
            PlatformFeatures.Current = PlatformFeatures.Desktop;
            VersionInfo.WebServer = null;
        }
        // The desktop program shows its build date, no deployment
        Assert.DoesNotContain(VersionInfo.Collect(), r => r.LabelKey == "InfoDeployed");
        Assert.Null(VersionInfo.HostingBadge);
    }

    private static (Window Window, DialogLayer Layer) LayerWindow()
    {
        var layer = new DialogLayer();
        var window = new Window { Width = 900, Height = 600, Content = new Grid { Children = { new Border(), layer } } };
        window.Show();
        return (window, layer);
    }

    [AvaloniaFact]
    public async Task AnOverlayDialogReturnsWhatItClosesWith()
    {
        var (window, layer) = LayerWindow();
        var shown = layer.ShowAsync(new UnsavedChangesDialogView("a.pdf"));
        await Pump();
        Assert.True(layer.IsVisible);
        Assert.True(layer.HasOpenDialog);
        var discard = layer.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Loc.T("DontSave")));
        await Click(window, discard);
        Assert.Equal(UnsavedChoice.Discard, await shown);
        Assert.False(layer.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EscapeClosesOnlyTheTopOverlayWithoutResult()
    {
        var (window, layer) = LayerWindow();
        var below = layer.ShowAsync(new UnsavedChangesDialogView("a.pdf"));
        var top = layer.ShowAsync(new MessageDialogView("Top", null));
        await Pump();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump();
        Assert.True(top.IsCompleted);
        Assert.Null(await top);
        Assert.False(below.IsCompleted);   // the dialog below is still open
        Assert.True(layer.HasOpenDialog);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump();
        Assert.Equal(UnsavedChoice.Cancel, await below); // its cancel button
        Assert.False(layer.HasOpenDialog);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AWindowDialogStillShowsItsContentAndFindsItsParts()
    {
        var dialog = new UnsavedChangesDialog("a.pdf");
        dialog.Show();
        await Pump();
        Assert.Equal(Loc.T("UnsavedTitle"), dialog.Title);
        Assert.NotNull(dialog.FindControl<TextBlock>("MessageText"));
        Assert.Contains("a.pdf", dialog.FindControl<TextBlock>("MessageText")!.Text);
        dialog.Close();
    }
}
