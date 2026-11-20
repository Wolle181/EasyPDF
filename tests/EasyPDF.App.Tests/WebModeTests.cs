using System;
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
using EasyPDF.App.Views;
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
            Assert.All(layer.GetVisualDescendants().OfType<RibbonButton>(), b => Assert.True(b.IsVisible));
            window.Close();
        }
        finally
        {
            PlatformFeatures.Current = PlatformFeatures.Desktop;
        }
    }

    [Fact]
    public void AboutListsTheWebAddressOfTheBrand()
    {
        var rows = VersionInfo.Collect();
        if (VersionInfo.WebAppUrl.Length > 0)
            Assert.Contains(rows, r => r.LabelKey == "InfoWebVersion" && r.Value == VersionInfo.WebAppUrl);
        else
            Assert.DoesNotContain(rows, r => r.LabelKey == "InfoWebVersion");
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
