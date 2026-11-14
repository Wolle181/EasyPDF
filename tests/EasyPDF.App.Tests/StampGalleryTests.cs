using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class StampGalleryTests
{
    /// <summary>A valid 1x1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static StackPanel Host(Flyout flyout) => (StackPanel)((ScrollViewer)flyout.Content!).Content!;

    [AvaloniaFact]
    public async Task GalleryShowsTheFolderPicturesSkipsBrokenOnesAndPicksOne()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("b-stamp.png"), Png);
        File.WriteAllBytes(dir.File("a-signature.png"), Png);
        File.WriteAllBytes(dir.File("broken.png"), [1, 2, 3]);
        File.WriteAllText(dir.File("notes.txt"), "not a picture");
        var settings = new AppSettings { Language = "en", StampFolder = dir.Path };
        AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        var stampButton = window.FindControl<EasyPDF.App.Controls.Ribbon.RibbonButton>("StampButton")!;
        Assert.False(stampButton.IsEnabled); // needs an open document
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        await Pump();
        Assert.True(stampButton.IsEnabled);

        var flyout = Assert.IsType<Flyout>(stampButton.Flyout);
        flyout.ShowAt(stampButton);
        await Pump();
        var host = Host(flyout);
        var items = ((WrapPanel)host.Children[0]).Children.OfType<Button>().ToList();
        Assert.Equal(["a-signature", "b-stamp"], items.Select(b => (string)((TextBlock)((StackPanel)b.Content!).Children[1]).Text!).ToArray());
        Assert.Contains(host.Children.OfType<TextBlock>(), t => t.Text == Loc.F("StampSkipped", 1));

        items[1].Command!.Execute(items[1].CommandParameter);
        Assert.Equal(dir.File("b-stamp.png"), window.ViewModel.PendingStampPath);
        Assert.Contains("b-stamp", window.ViewModel.StatusMessage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EmptyOrMissingFolderExplainsHowToFillItAndIsCreated()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "my stamps");
        var settings = new AppSettings { Language = "en", StampFolder = folder };
        AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        var stampButton = window.FindControl<EasyPDF.App.Controls.Ribbon.RibbonButton>("StampButton")!;
        var flyout = Assert.IsType<Flyout>(stampButton.Flyout);
        flyout.ShowAt(stampButton);
        await Pump();
        Assert.True(Directory.Exists(folder));
        Assert.Contains(Host(flyout).Children.OfType<TextBlock>(), t => t.Text == Loc.T("StampGalleryEmpty"));
        Assert.Null(window.ViewModel.PendingStampPath);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheSampleStampProvidedAtStartIsInTheGallery()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "my stamps");
        Directory.CreateDirectory(folder); // kept from an uninstalled version
        var settings = new AppSettings { Language = "en", StampFolder = folder, TemplateFolder = Path.Combine(dir.Path, "templates") };
        AppEnvironment.Apply(settings);
        Assert.True(SampleContent.Provide(settings));
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        var stampButton = window.FindControl<EasyPDF.App.Controls.Ribbon.RibbonButton>("StampButton")!;
        var flyout = Assert.IsType<Flyout>(stampButton.Flyout);
        flyout.ShowAt(stampButton);
        await Pump();
        Assert.True(File.Exists(Path.Combine(folder, "Stempel 'gescannt'.png")));
        var items = ((WrapPanel)Host(flyout).Children[0]).Children.OfType<Button>().ToList();
        Assert.Equal(["Eingegangen", "Received", "Stempel 'gescannt'"], items.Select(b => (string)((TextBlock)((StackPanel)b.Content!).Children[1]).Text!).ToArray());
        window.Close();
    }

    [AvaloniaFact]
    public async Task SubfoldersAreSectionsThatStayOpenAsTheUserLeftThem()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("logo.png"), Png);
        Directory.CreateDirectory(Path.Combine(dir.Path, "Nummern"));
        File.WriteAllBytes(Path.Combine(dir.Path, "Nummern", "01.png"), Png);
        File.WriteAllBytes(Path.Combine(dir.Path, "Nummern", "02.png"), Png);
        var settings = new AppSettings { Language = "en", StampFolder = dir.Path };
        AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        var stampButton = window.FindControl<EasyPDF.App.Controls.Ribbon.RibbonButton>("StampButton")!;
        var flyout = Assert.IsType<Flyout>(stampButton.Flyout);
        flyout.ShowAt(stampButton);
        await Pump();

        var host = Host(flyout);
        Assert.Single(((WrapPanel)host.Children[0]).Children.OfType<Button>()); // the folder's own pictures first
        var section = host.Children.OfType<Expander>().Single();
        Assert.Equal(Loc.F("StampGroupHeader", "Nummern", 2), section.Header);
        Assert.False(section.IsExpanded);
        section.IsExpanded = true;
        var numbers = ((WrapPanel)section.Content!).Children.OfType<Button>().ToList();
        numbers[1].Command!.Execute(numbers[1].CommandParameter);
        Assert.Equal(Path.Combine(dir.Path, "Nummern", "02.png"), window.ViewModel.PendingStampPath);
        flyout.Hide();
        await Pump();

        // Opened again (to pick the next number): the section is still open
        flyout.ShowAt(stampButton);
        await Pump();
        Assert.True(Host(flyout).Children.OfType<Expander>().Single().IsExpanded);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsViewModelSetsAndResetsTheStampFolder()
    {
        var settings = new AppSettings();
        var changes = 0;
        var vm = new SettingsViewModel(settings, () => changes++);
        Assert.Equal(EasyPDF.Core.Stamps.StampLibrary.DefaultFolder, vm.StampFolder);
        vm.SetStampFolder(Path.GetTempPath());
        Assert.Equal(Path.GetTempPath(), settings.StampFolder);
        Assert.Equal(Path.GetTempPath(), vm.StampFolder);
        vm.SetStampFolder(null);
        Assert.Equal("", settings.StampFolder);
        Assert.Equal(EasyPDF.Core.Stamps.StampLibrary.DefaultFolder, vm.StampFolder);
        Assert.Equal(2, changes);
    }

    [AvaloniaFact]
    public async Task ClickingTheStampButtonOpensTheGalleryWithTheSamples()
    {
        using var dir = new TempDir();
        var settings = new AppSettings { Language = "en", StampFolder = Path.Combine(dir.Path, "stamps"), TemplateFolder = Path.Combine(dir.Path, "templates") };
        AppEnvironment.Apply(settings);
        Assert.True(SampleContent.Provide(settings));
        var window = new MainWindow(settings) { Width = 1400, Height = 800 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        var stampButton = window.FindControl<EasyPDF.App.Controls.Ribbon.RibbonButton>("StampButton")!;
        var flyout = Assert.IsType<Flyout>(stampButton.Flyout);

        var p = stampButton.TranslatePoint(new Avalonia.Point(stampButton.Bounds.Width / 2, stampButton.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, Avalonia.Input.MouseButton.Left);
        window.MouseUp(p, Avalonia.Input.MouseButton.Left);
        await Pump();

        Assert.True(flyout.IsOpen);
        var host = Host(flyout);
        Assert.Contains(host.Children.OfType<Avalonia.Controls.Expander>(), e => (e.Header as string)!.Contains("(21)"));
        flyout.Hide();
        window.Close();
    }
}
