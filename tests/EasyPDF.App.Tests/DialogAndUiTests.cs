using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class RotateDialogViewModelTests
{
    private static RotateDialogViewModel Make(params int[] selected) =>
        new(new RotateDialogContext(10, 4, selected, 90));

    [Fact]
    public void DefaultsToClockwiseAndCurrentPage()
    {
        var vm = Make();
        var result = vm.BuildResult();
        Assert.Equal(90, result!.Degrees);
        Assert.Equal([4], result.Pages);
        Assert.True(vm.IsClockwise);
        Assert.False(vm.CanSelectSelected);
    }

    [Fact]
    public void SelectionBecomesTheDefaultScope()
    {
        var vm = Make(2, 3, 7);
        Assert.True(vm.CanSelectSelected);
        Assert.True(vm.IsSelected);
        Assert.Equal([2, 3, 7], vm.BuildResult()!.Pages);
    }

    [Fact]
    public void DirectionsAndAllPages()
    {
        var vm = Make();
        vm.IsCounterClockwise = true;
        Assert.Equal(-90, vm.Degrees);
        Assert.False(vm.IsClockwise);
        vm.IsHalfTurn = true;
        vm.IsAll = true;
        var r = vm.BuildResult();
        Assert.Equal(180, r!.Degrees);
        Assert.Equal(Enumerable.Range(0, 10), r.Pages);
    }

    [Fact]
    public void RangeIsValidatedLive()
    {
        var vm = Make();
        vm.IsRangeScope = true;
        Assert.False(vm.IsValid);            // empty
        Assert.NotEqual("", vm.ErrorText);
        Assert.Null(vm.BuildResult());

        vm.RangeText = "1-3, 5";
        Assert.True(vm.IsValid);
        Assert.Equal("", vm.ErrorText);
        Assert.Equal([0, 1, 2, 4], vm.BuildResult()!.Pages);

        vm.RangeText = "1-12";
        Assert.False(vm.IsValid);
        Assert.Contains("10", vm.ErrorText);
        vm.RangeText = "x";
        Assert.False(vm.IsValid);
        vm.RangeText = "5-2";
        Assert.False(vm.IsValid);

        vm.IsCurrent = true;                  // an invalid range does not matter when not used
        Assert.True(vm.IsValid);
    }
}

public class AppUiTests
{
    /// <summary>The round buttons in the brand header; the video button only when the brand comes with a video.</summary>
    private static string[] HeaderButtons => EasyPDF.App.Services.IntroVideo.IsAvailable
        ? ["SettingsButton", "VideoButton", "GuideButton", "InfoButton"]
        : ["SettingsButton", "GuideButton", "InfoButton"];

    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task RibbonHasTabsAndEveryButtonHasAnIconAndLabel()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        var ribbon = window.GetVisualDescendants().OfType<Ribbon>().Single();
        Assert.Equal(3, ribbon.Items.Count);
        var buttons = window.GetVisualDescendants().OfType<RibbonButton>().ToList();
        ribbon.SelectedIndex = 1; // only the selected tab is in the visual tree
        await Pump();
        buttons.AddRange(window.GetVisualDescendants().OfType<RibbonButton>());
        Assert.True(buttons.Distinct().Count() >= 18);
        // Icon-only buttons with a tooltip: settings, video, quick guide and Info in the header, zoom out/in in the status bar
        Assert.All(buttons.Where(b => b.Name is not ("SettingsButton" or "VideoButton" or "GuideButton" or "InfoButton" or "StatusZoomOut" or "StatusZoomIn") && b.Command != window.ViewModel.ActualSizeCommand), b =>
        {
            Assert.NotNull(b.Icon);
            Assert.False(string.IsNullOrEmpty(b.Label));
        });
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartPageHasTheSameGreyBackgroundAsTheReadingView()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        Assert.True(window.TryFindResource("Content.Background", window.ActualThemeVariant, out var brush));
        var view = window.GetVisualDescendants().OfType<EasyPDF.App.Controls.HorizontalPageView>().Single();
        var content = (Grid)view.GetVisualParent()!;
        Assert.Same(brush, content.Background);                // no document: the area behind the start page is grey too

        // The colour behind the start page and behind the pages is the same
        var frame = window.CaptureRenderedFrame()!;
        using (var fb = frame.Lock())
        {
            var corner = content.TranslatePoint(new Point(5, 5), window)!.Value;
            var pixel = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(fb.Address + (int)corner.Y * fb.RowBytes + (int)corner.X * 4, pixel, 0, 4);
            var expected = ((ISolidColorBrush)brush!).Color;
            Assert.Equal(new[] { expected.R, expected.G, expected.B }, fb.Format == Avalonia.Platform.PixelFormat.Bgra8888
                ? new[] { pixel[2], pixel[1], pixel[0] } : new[] { pixel[0], pixel[1], pixel[2] });
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task PrimaryBandInBothThemesAndDarkModeIsNotPureBlack()
    {
        try
        {
            var primary = EasyPDF.App.Styles.BrandColors.Primary;
            AppEnvironment.ApplyTheme(ThemeSetting.Dark);
            var window = new MainWindow { Width = 1000, Height = 700 };
            window.Show();
            await Pump();
            var band = (Grid)window.FindControl<Ribbon>("MainRibbon")!.GetVisualParent()!;
            Assert.Equal(primary, ((ISolidColorBrush)band.Background!).Color);      // the brand band stays primary in dark mode
            var background = ((ISolidColorBrush)window.Background!).Color;
            Assert.InRange(background.R, 0x10, 0x20);                            // slightly lighter than Fluent's pure black
            Assert.InRange(background.B, 0x10, 0x24);
            var footer = ((ISolidColorBrush)window.FindControl<Border>("StatusBar")!.Background!).Color;
            Assert.True(footer.R < 0x30 && footer.B < 0x30);                     // dark status bar, no light stripe
            window.Close();

            AppEnvironment.ApplyTheme(ThemeSetting.Light);
            window = new MainWindow { Width = 1000, Height = 700 };
            window.Show();
            await Pump();
            band = (Grid)window.FindControl<Ribbon>("MainRibbon")!.GetVisualParent()!;
            Assert.Equal(primary, ((ISolidColorBrush)band.Background!).Color);
            footer = ((ISolidColorBrush)window.FindControl<Border>("StatusBar")!.Background!).Color;
            Assert.True(footer.R > 0xE8 && footer.G > 0xE8 && footer.B > 0xE8);  // quiet light grey in light mode
            window.Close();
        }
        finally
        {
            AppEnvironment.ApplyTheme(ThemeSetting.System);
        }
    }

    [AvaloniaFact]
    public async Task StatusBarShowsTheRunningOperationWithProgress()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        await Pump();
        var panel = window.FindControl<StackPanel>("BusyPanel")!;
        Assert.False(panel.IsVisible);

        var vm = window.ViewModel;
        vm.BusyText = Loc.F("BusyExtracting", Loc.Pages(3));
        vm.Progress = 0.42;
        vm.IsBusy = true;
        await Pump();
        Assert.True(panel.IsVisible);
        var texts = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Extracting 3 pages…", texts);
        Assert.Contains("42 %", texts);
        Assert.Equal(0.42, panel.GetVisualDescendants().OfType<ProgressBar>().Single().Value);
        vm.IsBusy = false;
        await Pump();
        Assert.False(panel.IsVisible);
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task MainWindowAndDialogsShowTheAppIcon()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        Assert.NotNull(window.Icon);                           // title bar / taskbar

        var about = new EasyPDF.App.Dialogs.AboutWindow();
        about.Show();
        await Pump();
        Assert.NotNull(about.Icon);
        var image = about.FindControl<Image>("AppIcon")!;
        Assert.NotNull(image.Source);                          // big icon next to the program name
        Assert.Equal(128, image.Source!.Size.Width);
        Assert.True(image.Bounds.Width >= 64);
        // It is the brand mark of the header (tools/icon/make_icon.py) in the brand colours: accent frame, dark primary inside
        // (fails after a change of the brand colours until the icons are drawn again: python tools/icon/make_icon.py).
        // Made from a logo file (branding/<brand>/icons/logo.png) instead, it has the logo's colours.
        var bitmap = (Avalonia.Media.Imaging.Bitmap)image.Source;
        if (!System.IO.File.Exists(System.IO.Path.Combine(RepoPaths.BrandDir, "icons", "logo.png")))
            AssertIconHasBrandColors(bitmap);
        about.Close();
        window.Close();
    }

    private static void AssertIconHasBrandColors(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        AssertNear(EasyPDF.App.Styles.BrandColors.Accent, PixelAt(bitmap, 64, 7));     // a little blurred by downscaling
        AssertNear(EasyPDF.App.Styles.BrandColors.Shade(EasyPDF.App.Styles.BrandBase.Primary, lighten: -0.03), PixelAt(bitmap, 16, 64));
    }

    private static void AssertNear(Color expected, Color actual) =>
        Assert.True(System.Math.Abs(expected.R - actual.R) <= 12 && System.Math.Abs(expected.G - actual.G) <= 12 && System.Math.Abs(expected.B - actual.B) <= 12,
                    $"Expected about {expected}, got {actual}");

    private static Color PixelAt(Avalonia.Media.Imaging.Bitmap bitmap, int x, int y)
    {
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), buffer, 4, 4);
            var b = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(buffer, b, 0, 4);
            return bitmap.Format == Avalonia.Platform.PixelFormats.Rgba8888
                ? Color.FromRgb(b[0], b[1], b[2])
                : Color.FromRgb(b[2], b[1], b[0]);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    [AvaloniaFact]
    public async Task PageSidebarFollowsTheReadingViewAndJumpsOnClick()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 12));
        await Pump(400);
        var vm = window.ViewModel;
        var panel = window.FindControl<Border>("PageSidebar")!;
        var sidebar = window.FindControl<EasyPDF.App.Controls.ThumbnailGrid>("SidebarView")!;
        Assert.True(panel.IsVisible);
        Assert.Equal(0, sidebar.ActivePage);

        vm.GoToPage(7);                                        // reading further: the sidebar marks the page
        await Pump(400);
        Assert.Equal(7, vm.CurrentPage);
        Assert.Equal(7, sidebar.ActivePage);
        Assert.True(sidebar.PageIndexAt(new Point(sidebar.Bounds.Width / 2, sidebar.Bounds.Height / 2)) is >= 6 and <= 8); // and scrolls along

        // A click on a thumbnail shows that page in the reading view
        var origin = sidebar.TranslatePoint(new Point(0, 0), window)!.Value;
        var y = Enumerable.Range(0, (int)sidebar.Bounds.Height).First(v => sidebar.PageIndexAt(new Point(sidebar.Bounds.Width / 2, v)) == 8);
        var p = new Point(origin.X + sidebar.Bounds.Width / 2, origin.Y + y + 20);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump(400);
        Assert.Equal(8, vm.CurrentPage);
        Assert.Equal(8, window.GetVisualDescendants().OfType<EasyPDF.App.Controls.HorizontalPageView>().Single().CurrentPage);
        Assert.Empty(vm.SelectedIds);                          // navigating does not select pages

        vm.ShowOrganizeCommand.Execute(null);                  // Organize mode shows thumbnails anyway
        Assert.False(panel.IsVisible);
        vm.ShowReadingCommand.Execute(null);
        Assert.True(panel.IsVisible);
        vm.TogglePageSidebarCommand.Execute(null);             // View > Thumbnails switches it off
        Assert.False(panel.IsVisible);
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task StatusBarZoomButtonsAndMenuWork()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        // Headless tooltips are overlays right under the pointer and would swallow the next click on the same spot
        ToolTip.SetServiceEnabled(window, false);
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 6));
        await Pump();
        var vm = window.ViewModel;
        vm.ZoomToCommand.Execute(100.0);
        await Pump();

        void Click(string name)
        {
            var button = window.FindControl<Button>(name)!;
            var p = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
        }
        Click("StatusZoomIn");
        await Pump();
        Assert.Equal(125, vm.EffectiveZoomPercent, 1);
        Click("StatusZoomOut");
        Click("StatusZoomOut");                                // two quick clicks: two steps, even before the view re-laid out
        Assert.Equal(75, vm.CustomZoomPercent);
        await Pump();
        Assert.Equal(75, vm.EffectiveZoomPercent, 1);

        var menu = (MenuFlyout)window.FindControl<Button>("StatusZoomMenu")!.Flyout!;
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(11, items.Count);                         // 8 steps + 3 fit modes
        var item200 = items.Single(i => (string?)i.Header == "200 %");
        item200.Command!.Execute(item200.CommandParameter);
        await Pump();
        Assert.Equal(200, vm.EffectiveZoomPercent, 1);
        Assert.Equal("200 %", vm.StatusZoomText);
        var fitHeight = items.Single(i => (string?)i.Header == Loc.T("BtnFitHeight"));
        Assert.True(fitHeight.Command!.CanExecute(null));

        vm.ShowOrganizeCommand.Execute(null);                  // Organize mode: the same controls size the thumbnails
        item200.Command.Execute(item200.CommandParameter);
        Assert.Equal(200, vm.ThumbnailZoomPercent);
        Assert.False(fitHeight.Command.CanExecute(null));
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task StatusBarShowsAndSwitchesTheCurrentMode()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        var modeSwitch = window.FindControl<Border>("ModeSwitch")!;
        Assert.False(modeSwitch.IsVisible);                    // nothing to switch without a document
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        await Pump();
        Assert.True(modeSwitch.IsVisible);
        var buttons = modeSwitch.GetVisualDescendants().OfType<RibbonButton>().ToList();
        var reading = buttons.Single(b => b.Classes.Contains("reading"));
        var organize = buttons.Single(b => b.Classes.Contains("organize"));
        Assert.True(reading.IsActive);
        Assert.False(organize.IsActive);

        var ribbon = window.GetVisualDescendants().OfType<Ribbon>().Single();
        ribbon.SelectedIndex = 2;                              // View tab open: the status bar still tells the mode
        await Pump();
        var p = organize.TranslatePoint(new Point(organize.Bounds.Width / 2, organize.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();
        Assert.True(window.ViewModel.IsOrganizeMode);
        Assert.True(organize.IsActive);
        Assert.False(reading.IsActive);

        // The active mode is a filled in the primary colour button
        Assert.True(window.TryFindResource("Mode.Active", window.ActualThemeVariant, out var brush));
        var presenter = organize.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(c => c.Name == "PART_ContentPresenter");
        Assert.Equal(((ISolidColorBrush)brush!).Color, ((ISolidColorBrush)presenter.Background!).Color);
        var label = organize.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Loc.T("BtnOrganizeMode"));
        Assert.Equal(Colors.White, ((ISolidColorBrush)label.Foreground!).Color);   // white text on primary
        Assert.Contains("+2)", (string)ToolTip.GetTip(organize)!);   // shortcut shown as "Ctrl+2", not "Ctrl+D2"
        window.Close();
        await window.ViewModel.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task RibbonTooltipsShowLabelAndShortcutOnEveryTab()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        var ribbon = window.GetVisualDescendants().OfType<Ribbon>().Single();
        var buttons = new List<RibbonButton>();
        for (var tab = 0; tab < ribbon.Items.Count; tab++)
        {
            ribbon.SelectedIndex = tab;
            await Pump();
            buttons.AddRange(window.GetVisualDescendants().OfType<RibbonButton>());
        }
        foreach (var b in buttons.Where(b => !string.IsNullOrEmpty(b.Label)))
            Assert.StartsWith(b.Label, (string)ToolTip.GetTip(b)!);

        string Tip(string label) => (string)ToolTip.GetTip(buttons.First(b => b.Label == label))!;
        Assert.Contains("+O)", Tip(Loc.T("BtnOpen")));
        Assert.Contains("+Z)", Tip(Loc.T("BtnUndo")));
        Assert.Contains("+R)", Tip(Loc.T("BtnRotateRight")));
        Assert.Contains("++)", Tip(Loc.T("BtnZoomIn")));          // OemPlus is shown as "+"
        Assert.Equal(Loc.T("BtnInsertBefore"), Tip(Loc.T("BtnInsertBefore"))); // no shortcut -> label only
        window.Close();
    }

    [AvaloniaFact]
    public async Task AllRibbonTabsShareOneSoberLook()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        var ribbon = window.GetVisualDescendants().OfType<Ribbon>().Single();
        Color Resource(string key)
        {
            Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value));
            return ((ISolidColorBrush)value!).Color;
        }
        for (var tab = 0; tab < ribbon.Items.Count; tab++)
        {
            ribbon.SelectedIndex = tab;
            await Pump();
            // White panel under the band, topped by the accent line (the same on every tab)
            var panel = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("ribbonPanel"));
            Assert.Equal(Resource("Ribbon.Panel"), ((ISolidColorBrush)panel.Background!).Color);
            Assert.Equal(EasyPDF.App.Styles.BrandColors.Accent, ((ISolidColorBrush)panel.BorderBrush!).Color);
            Assert.Equal(3, panel.BorderThickness.Top);
            var caption = window.GetVisualDescendants().OfType<RibbonGroup>().SelectMany(g => g.GetVisualDescendants().OfType<TextBlock>())
                .First(t => t.Text == Loc.T(tab == 1 ? "GroupMode" : tab == 2 ? "GroupZoom" : "GroupFile"));
            Assert.Equal(Resource("Ribbon.Caption"), ((ISolidColorBrush)caption.Foreground!).Color);

            // Selected tab: white text on a lighter primary shade, no Fluent underline pipe
            var tabItem = (TabItem)ribbon.ContainerFromIndex(tab)!;
            var frame = tabItem.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_LayoutRoot");
            Assert.Equal(Resource("Band.Selected"), ((ISolidColorBrush)frame.Background!).Color);
            var header = tabItem.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(c => c.Name == "PART_ContentPresenter");
            Assert.Equal(Colors.White, ((ISolidColorBrush)header.Foreground!).Color);
            Assert.False(tabItem.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_SelectedPipe").IsVisible);
        }

        // Settings, help and info sit on the primary band: their icons are drawn white
        foreach (var name in HeaderButtons)
        {
            var paths = window.FindControl<RibbonButton>(name)!.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().ToList();
            Assert.NotEmpty(paths);
            Assert.All(paths, p => Assert.Equal(Colors.White, ((ISolidColorBrush)p.Stroke!).Color));
        }
        window.Close();
    }

    /// <summary>German labels are long ("Zusammenführen…"): large ribbon buttons break them between words only.</summary>
    [AvaloniaFact]
    public async Task LargeRibbonLabelsBreakOnlyBetweenWords()
    {
        var old = CultureInfo.CurrentUICulture;
        Loc.SetCulture(new CultureInfo("de"));
        try
        {
            var window = new MainWindow { Width = 1400, Height = 700 };
            window.Show();
            using var dir = new TempDir();
            await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
            await Pump();
            var ribbon = window.FindControl<Ribbon>("MainRibbon")!;
            var labels = new List<TextBlock>();
            for (var tab = 0; tab < 2; tab++)
            {
                ribbon.SelectedIndex = tab;
                await Pump();
                labels.AddRange(ribbon.GetVisualDescendants().OfType<RibbonButton>().Where(b => b.IsShownLarge && b.IsEffectivelyVisible)
                    .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>()));
            }
            Assert.Contains(labels, t => t.Text == "Zusammenführen…");
            Assert.All(labels, t =>
            {
                var lines = t.TextLayout.TextLines;
                foreach (var line in lines.Take(lines.Count - 1))                    // every break follows a space
                    Assert.True(t.Text![line.FirstTextSourceIndex + line.Length - 1] == ' ', $"'{t.Text}' is broken inside a word");
            });
            window.Close();
            await window.ViewModel.Document!.DisposeAsync();
        }
        finally
        {
            Loc.SetCulture(old);
        }
    }

    /// <summary>Groups reduced to one column of icons are narrower than their caption: neighbouring captions must still keep a gap.</summary>
    [AvaloniaFact]
    public async Task GroupCaptionsNeverRunIntoEachOther()
    {
        var old = CultureInfo.CurrentUICulture;
        Loc.SetCulture(new CultureInfo("de"));
        try
        {
            var window = new MainWindow { Width = 1200, Height = 700 };
            window.Show();
            using var dir = new TempDir();
            await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
            await Pump();
            var ribbon = window.FindControl<Ribbon>("MainRibbon")!;
            foreach (var width in new[] { 900, 1000, 1100, 1200, 1400, 1600 })
            {
                window.Width = width;
                for (var tab = 0; tab < ribbon.Items.Count; tab++)
                {
                    ribbon.SelectedIndex = tab;
                    await Pump();
                    var captions = ribbon.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.Name == "PART_CaptionText" && t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                        .Select(t => (t.Text, Left: t.TranslatePoint(new Point(0, 0), window)!.Value.X, t.Bounds.Width))
                        .OrderBy(c => c.Left)
                        .ToList();
                    Assert.True(captions.Count >= 2);
                    for (var i = 1; i < captions.Count; i++)
                    {
                        var gap = captions[i].Left - (captions[i - 1].Left + captions[i - 1].Width);
                        Assert.True(gap >= 6, $"{width} px, tab {tab}: \"{captions[i - 1].Text}\" and \"{captions[i].Text}\" are {gap:0.#} px apart");
                    }
                }
            }
            window.Close();
            await window.ViewModel.Document!.DisposeAsync();
        }
        finally
        {
            Loc.SetCulture(old);
        }
    }

    [AvaloniaFact]
    public async Task BrandHeaderShowsNameSubtitleAndHelpAboveTheTabs()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            var window = new MainWindow { Width = 1000, Height = 700 };
            window.Show();
            await Pump();
            var header = window.FindControl<Border>("BrandHeader")!;
            var ribbon = window.FindControl<Ribbon>("MainRibbon")!;
            Assert.True(header.IsVisible);
            var top = header.TranslatePoint(new Point(0, header.Bounds.Height), window)!.Value.Y;
            Assert.True(top <= ribbon.TranslatePoint(new Point(0, 0), window)!.Value.Y);   // directly above the tabs

            var title = window.FindControl<TextBlock>("BrandTitle")!;
            var subtitle = window.FindControl<TextBlock>("BrandSubtitle")!;
            Assert.Equal(Loc.AppName, title.Text);                 // shown name (brand profile)
            Assert.Equal("Read, organize and combine PDFs – locally and offline", subtitle.Text);
            Assert.Equal(Colors.White, ((ISolidColorBrush)title.Foreground!).Color);

            // The mark is framed in the accent colour; settings, help and info sit in the header, settings left of help
            var mark = window.FindControl<Border>("BrandMark")!;
            Assert.Equal(EasyPDF.App.Styles.BrandColors.Accent, ((ISolidColorBrush)mark.BorderBrush!).Color);
            foreach (var name in HeaderButtons)
                Assert.Contains(header, window.FindControl<RibbonButton>(name)!.GetVisualAncestors());
            var settings = window.FindControl<RibbonButton>("SettingsButton")!;
            var guide = window.FindControl<RibbonButton>("GuideButton")!;
            Assert.Same(window.ViewModel.ShowSettingsCommand, settings.Command);
            Assert.True(settings.IsEffectivelyEnabled); // also without a document
            Assert.True(settings.TranslatePoint(new Point(0, 0), window)!.Value.X < guide.TranslatePoint(new Point(0, 0), window)!.Value.X);

            Loc.SetCulture(new CultureInfo("de"));                 // the subtitle follows the language live
            await Pump();
            Assert.Equal("PDFs lesen, ordnen und zusammenführen – lokal und offline", subtitle.Text);
            window.Close();
        }
        finally
        {
            Loc.SetCulture(old);
        }
    }

    [AvaloniaFact]
    public async Task RibbonButtonsAreRoundIconButtons()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        await Pump();
        Color Resource(string key)
        {
            Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value));
            return ((ISolidColorBrush)value!).Color;
        }
        Border Circle(RibbonButton b) => b.GetVisualDescendants().OfType<Border>().Single(c => c.Classes.Contains("iconCircle"));
        RibbonButton ByCommand(System.Windows.Input.ICommand c) =>
            window.GetVisualDescendants().OfType<RibbonButton>().First(b => b.Command == c && b.IsEffectivelyVisible);

        // Every ribbon button with an icon has its circle; buttons with a fixed icon size (status bar) do not
        var ribbon = window.FindControl<Ribbon>("MainRibbon")!;
        var inRibbon = ribbon.GetVisualDescendants().OfType<RibbonButton>().Where(b => b.Icon != null && b.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(inRibbon);
        Assert.All(inRibbon, b => Assert.Equal(b.IsShownLarge ? 38 : 22, Circle(b).Width));   // large and small circles
        Assert.DoesNotContain(window.FindControl<RibbonButton>("StatusZoomIn")!.GetVisualDescendants().OfType<Border>(), c => c.Classes.Contains("iconCircle"));

        // Settings, help and info on the primary band are round too: a light ring, filled on hover instead of a rectangle
        foreach (var name in HeaderButtons)
        {
            var button = window.FindControl<RibbonButton>(name)!;
            Assert.Equal(32, Circle(button).Width);
            Assert.Equal(Colors.Transparent, ((ISolidColorBrush)Circle(button).Background!).Color);
            Assert.Equal(Resource("Band.CircleBorder"), ((ISolidColorBrush)Circle(button).BorderBrush!).Color);
        }
        var info = window.FindControl<RibbonButton>("InfoButton")!;
        window.MouseMove(info.TranslatePoint(new Point(info.Bounds.Width / 2, info.Bounds.Height / 2), window)!.Value);
        await Pump();
        Assert.Equal(Resource("Band.Selected"), ((ISolidColorBrush)Circle(info).Background!).Color);
        var infoPresenter = info.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(c => c.Name == "PART_ContentPresenter");
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)infoPresenter.Background!).Color);
        window.MouseMove(new Point(500, 500));
        await Pump();

        // Open is the primary circle, Save the accent one
        Assert.Equal(Resource("Circle.Active"), ((ISolidColorBrush)Circle(ByCommand(window.ViewModel.OpenCommand)).Background!).Color);
        Assert.Equal(EasyPDF.App.Styles.BrandColors.Accent, ((ISolidColorBrush)Circle(ByCommand(window.ViewModel.SaveCommand)).Background!).Color);

        // A switched-on button: filled circle with white lines
        window.ViewModel.SelectTextToolCommand.Execute(null);
        await Pump();
        var text = ByCommand(window.ViewModel.SelectTextToolCommand);
        Assert.True(text.IsActive);
        Assert.Equal(Resource("Circle.Active"), ((ISolidColorBrush)Circle(text).Background!).Color);
        Assert.All(text.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(p => p.IsVisible),
            p => Assert.Equal(Colors.White, ((ISolidColorBrush)p.Stroke!).Color));

        // Hover lights the circle, not a rectangle around the button
        var print = ByCommand(window.ViewModel.PrintCommand);
        window.MouseMove(print.TranslatePoint(new Point(print.Bounds.Width / 2, print.Bounds.Height / 2), window)!.Value);
        await Pump();
        Assert.Equal(Resource("Ribbon.Hover"), ((ISolidColorBrush)Circle(print).Background!).Color);
        var presenter = print.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(c => c.Name == "PART_ContentPresenter");
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)presenter.Background!).Color);
        window.Close();
        await window.ViewModel.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TheEmbeddedBarlowTypefaceIsUsed()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        var label = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == Loc.T("GroupFile"));
        var typeface = new Typeface(label.FontFamily);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs));   // a wrong font path would fall back silently
        Assert.Equal("Barlow", glyphs!.FamilyName);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheDefaultButtonOfADialogIsTheAccentColouredMainAction()
    {
        var dialog = new EasyPDF.App.Dialogs.UnsavedChangesDialog("a.pdf");
        dialog.Show();
        await Pump();
        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
        var save = buttons.Single(b => b.IsDefault);
        Assert.Equal(EasyPDF.App.Styles.BrandColors.Accent, ((ISolidColorBrush)save.Background!).Color);
        Assert.All(buttons.Where(b => !b.IsDefault), b => Assert.NotEqual(EasyPDF.App.Styles.BrandColors.Accent, ((ISolidColorBrush)b.Background!).Color));
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task SelectedPagesInOrganizeAreFilledInThePrimaryColor()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 4));
        window.ViewModel.ShowOrganizeCommand.Execute(null);
        await Pump();
        var view = window.FindControl<EasyPDF.App.Controls.ThumbnailGrid>("OrganizeView")!;

        int PrimaryPixels()
        {
            var primary = EasyPDF.App.Styles.BrandColors.Primary;
            var frame = window.CaptureRenderedFrame()!;
            using var fb = frame.Lock();
            var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
            var row = new byte[fb.RowBytes];
            var count = 0;
            for (var y = (int)origin.Y; y < (int)(origin.Y + view.Bounds.Height); y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
                for (var x = (int)origin.X; x < (int)(origin.X + view.Bounds.Width); x++)
                    // byte order is RGBA or BGRA depending on the platform
                    if (row[x * 4 + 1] == primary.G && (row[x * 4] == primary.B && row[x * 4 + 2] == primary.R || row[x * 4] == primary.R && row[x * 4 + 2] == primary.B))
                        count++;
            }
            return count;
        }

        window.ViewModel.SetSelection([]);
        await Pump();
        var before = PrimaryPixels();
        window.ViewModel.SelectAllCommand.Execute(null);
        await Pump();
        var after = PrimaryPixels();
        Assert.True(after > before + 4 * 1000, $"{before} -> {after}");   // every selected page sits on a primary card
        window.Close();
        await window.ViewModel.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task RibbonCollapsesAndExpands()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        window.ViewModel.ToggleRibbonCommand.Execute(null);
        await Pump();
        Assert.True(window.GetVisualDescendants().OfType<Ribbon>().Single().IsCollapsed);
        window.ViewModel.ToggleRibbonCommand.Execute(null);
        Assert.False(window.ViewModel.IsRibbonCollapsed);
        window.Close();
    }

    private static async Task Click(Window window, Visual target, int clicks = 1)
    {
        var p = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        for (var i = 0; i < clicks; i++)
        {
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
        }
        await Pump();
    }

    [AvaloniaFact]
    public async Task TheArrowInTheTabRowFoldsTheRibbonAndTellsTheShortcut()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        var collapse = window.FindControl<RibbonButton>("CollapseRibbonButton")!;
        var expand = window.FindControl<RibbonButton>("ExpandRibbonButton")!;
        Assert.True(collapse.IsVisible);
        Assert.False(expand.IsVisible);
        Assert.Equal(Loc.T("BtnCollapseRibbon") + " (Ctrl+F1)", ToolTip.GetTip(collapse));  // found without the menu bar

        await Click(window, collapse);
        Assert.True(window.ViewModel.IsRibbonCollapsed);
        Assert.False(collapse.IsVisible);
        Assert.True(expand.IsVisible);                         // still in the (now only) tab row
        Assert.Equal(Loc.T("BtnExpandRibbon") + " (Ctrl+F1)", ToolTip.GetTip(expand));

        await Click(window, expand);
        Assert.False(window.ViewModel.IsRibbonCollapsed);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ASingleClickOnAnyTabOpensTheCollapsedRibbon()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        await Pump();
        var ribbon = window.FindControl<Ribbon>("MainRibbon")!;
        TabItem Tab(int i) => (TabItem)ribbon.ContainerFromIndex(i)!;

        window.ViewModel.ToggleRibbonCommand.Execute(null);
        await Pump();
        await Click(window, Tab(0));                           // the tab that is already selected
        Assert.False(window.ViewModel.IsRibbonCollapsed);

        window.ViewModel.ToggleRibbonCommand.Execute(null);
        await Pump();
        await Click(window, Tab(2));                           // another tab: opens and shows that tab
        Assert.False(window.ViewModel.IsRibbonCollapsed);
        Assert.Equal(2, ribbon.SelectedIndex);

        // A double-click still folds an open ribbon away; on a folded one it ends open (the first click opened it)
        await Task.Delay(900, TestContext.Current.CancellationToken);
        await Click(window, Tab(2), clicks: 2);
        Assert.True(window.ViewModel.IsRibbonCollapsed);
        await Task.Delay(900, TestContext.Current.CancellationToken);
        await Click(window, Tab(1), clicks: 2);
        Assert.False(window.ViewModel.IsRibbonCollapsed);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SwitchingLanguageUpdatesRibbonAndTitleLive()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            var settings = new AppSettings { Language = "en" };
            AppEnvironment.Apply(settings);
            var window = new MainWindow(settings) { Width = 1000, Height = 700 };
            window.Show();
            await Pump();
            var open = window.GetVisualDescendants().OfType<RibbonButton>().First(b => b.Command == window.ViewModel.OpenCommand);
            Assert.Equal("Open…", open.Label);
            Assert.Equal("No document open", window.ViewModel.StatusPageText);

            AppEnvironment.ApplyLanguage("de");
            window.ViewModel.RefreshTexts();
            await Pump();
            Assert.Equal("Öffnen…", open.Label);
            Assert.Equal("Kein Dokument geöffnet", window.ViewModel.StatusPageText);
            window.Close();
        }
        finally
        {
            Loc.SetCulture(old);
        }
    }

    [AvaloniaFact]
    public void SettingsViewModelAppliesThemeAndLanguageImmediately()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            var settings = new AppSettings();
            var changes = 0;
            var vm = new SettingsViewModel(settings, () => changes++);
            vm.ThemeIndex = 2;
            Assert.Equal(ThemeSetting.Dark, settings.Theme);
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            vm.ThemeIndex = 1;
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
            vm.ThemeIndex = 0;
            Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);

            vm.LanguageIndex = 2;
            Assert.Equal("de", settings.Language);
            Assert.Equal("de", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            Assert.Equal("Datei", Loc.T("MenuFile"));
            vm.LanguageIndex = 1;
            Assert.Equal("File", Loc.T("MenuFile"));
            Assert.Equal(5, changes);
        }
        finally
        {
            Loc.SetCulture(old);
            AppEnvironment.ApplyTheme(ThemeSetting.System);
        }
    }

    [AvaloniaFact]
    public async Task PageContextMenuOffersInsertRotateAndDelete()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        await Pump();
        var view = window.GetVisualDescendants().OfType<EasyPDF.App.Controls.HorizontalPageView>().Single();
        var menu = view.ContextMenu;
        Assert.NotNull(menu);
        var headers = menu!.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();
        Assert.Equal(14, headers.Count);
        Assert.Contains(Loc.T("CtxReplacePages"), headers);
        Assert.Contains(headers, h => h != null && h.Contains("before"));
        Assert.Contains("Recognize text (OCR)…", headers);
        Assert.Contains("Copy text of selected pages", headers);
        Assert.Contains(headers, h => h != null && h.Contains("after"));
        Assert.Contains(headers, h => h != null && h.Contains("Extract"));
        Assert.Same(menu.GetType(), window.FindControl<EasyPDF.App.Controls.ThumbnailGrid>("OrganizeView")!.ContextMenu!.GetType());
        window.Close();
        await window.ViewModel.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task ClickingAPageInTheReadingViewMarksItAsTheTargetOfTheNextEdit()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 3));
        await Pump();
        var vm = window.ViewModel;
        var view = window.GetVisualDescendants().OfType<EasyPDF.App.Controls.HorizontalPageView>().Single();
        Assert.Equal(-1, view.HighlightedPage);

        // Finds a window point over the given page by scanning the middle of the view
        Point Over(int page)
        {
            var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
            for (var x = 1.0; x < view.Bounds.Width; x += 3)
                if (view.PageIndexAt(new Point(x, view.Bounds.Height / 2)) == page)
                    return new Point(origin.X + x, origin.Y + view.Bounds.Height / 2);
            throw new Xunit.Sdk.XunitException($"page {page} is not visible");
        }

        var p = Over(1);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Assert.Equal(1, view.HighlightedPage);                 // red frame right after the click
        Assert.Equal(vm.Document!.Pages[1].Id, vm.MarkedPageId);

        vm.MovePages([1], 0);                                  // the mark moves with its page
        Assert.Equal(0, view.HighlightedPage);
        vm.RotateRightCommand.Execute(null);                   // no explicit page -> the marked one
        Assert.Equal(new[] { 90, 0, 0 }, vm.Document!.Pages.Select(x => x.ExtraRotation).ToArray());
        vm.UndoCommand.Execute(null);                          // (a rotated current page would change the fit zoom)
        await Pump();

        var q = Over(1);
        window.MouseDown(q, MouseButton.Left);
        window.MouseUp(q, MouseButton.Left);
        Assert.Equal(1, view.HighlightedPage);                 // clicking another page moves the mark

        window.MouseDown(q, MouseButton.Left);
        window.MouseUp(q, MouseButton.Left);
        Assert.Equal(-1, view.HighlightedPage);                // clicking the marked page again removes the mark
        Assert.Equal(-1, vm.MarkedPageId);

        window.MouseDown(q, MouseButton.Right);
        window.MouseUp(q, MouseButton.Right);
        Assert.Equal(1, view.HighlightedPage);                 // a right click marks the page ...
        window.MouseDown(q, MouseButton.Right);
        window.MouseUp(q, MouseButton.Right);
        Assert.Equal(1, view.HighlightedPage);                 // ... and keeps it marked (the context menu acts on it)
        window.Close();
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public void VersionInfoContainsVersionAndPdfiumBuild()
    {
        var info = VersionInfo.Collect().ToDictionary(i => i.LabelKey, i => i.Value);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+( \([0-9a-f]+(-dirty)?\))?$", info["InfoVersion"]);   // 1.0.0.97 (3fa9c1d2)
        Assert.Equal("1.0.0.97 (3fa9c1d2-dirty)", VersionInfo.Display("1.0.0.97+3fa9c1d2-dirty"));
        Assert.Equal("1.0.0.97", VersionInfo.Display("1.0.0.97"));
        Assert.Equal("155.0.8057", info["InfoPdfium"]);
        Assert.Contains("MIT", info["InfoLicense"]);
        var author = RepoPaths.BrandProp("AppAuthor");                                 // set in the brand profile only
        Assert.False(string.IsNullOrWhiteSpace(author));
        Assert.Equal(author, info["InfoAuthor"]);
        Assert.Contains(author, Dialogs.AboutWindowText());                            // also in "Copy version info"
        Assert.StartsWith(Loc.AppName + "\n", Dialogs.AboutWindowText());                  // shown product name
        Assert.StartsWith("RapidOcrNet 4.2.0, ONNX Runtime 1.29", info["InfoOcr"]);
        Assert.Equal(RepoPaths.BrandProp("Copyright"), info["InfoCopyright"]);          // the brand's copyright line
        Assert.Equal(RepoPaths.BrandProp("Company"), VersionInfo.Company);
    }

    [AvaloniaFact]
    public async Task AboutShowsTheCompanyLogoOfTheBrand()
    {
        // branding/<brand>/icons/company-logo.svg, embedded as Assets/CompanyLogo.svg; no logo, no frame
        var file = System.IO.Path.Combine(RepoPaths.BrandDir, "icons", "company-logo.svg");
        var about = new EasyPDF.App.Dialogs.AboutWindow();
        about.Show();
        await Pump();
        var frame = about.FindControl<Border>("CompanyLogoFrame")!;
        var logo = about.FindControl<Image>("CompanyLogo")!;
        Assert.Equal(System.IO.File.Exists(file), frame.IsVisible);
        if (frame.IsVisible)
        {
            Assert.IsType<DrawingImage>(logo.Source);                           // vector graphics: sharp at every scaling
            Assert.InRange(logo.Bounds.Width, 40, 112);                        // fits beside the program name
            Assert.True(logo.Bounds.Height <= 60);
            Assert.Equal(VersionInfo.Company, ToolTip.GetTip(frame));
        }

        about.ShowCompanyLogo(null);
        Assert.False(frame.IsVisible);
        about.Close();
    }

    [AvaloniaFact]
    public void TheCompanyLogosOfAllBrandsCanBeDrawn()
    {
        // Only the selected brand is built: read the others' logos here, so a broken file shows up before its build
        foreach (var file in System.IO.Directory.GetFiles(System.IO.Path.Combine(RepoPaths.Root, "branding"), "company-logo.svg", System.IO.SearchOption.AllDirectories))
        {
            using var stream = System.IO.File.OpenRead(file);
            var image = EasyPDF.App.Styles.SimpleSvg.Load(stream);
            Assert.True(image.Size.Width > image.Size.Height && image.Size.Height > 0, file);
            Assert.True(((DrawingGroup)image.Drawing!).Children.Count > 2, file);
        }
    }

    [AvaloniaFact]
    public void SimpleSvgDrawsPathsWithTheirColourPositionAndFillRule()
    {
        const string Svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50">
            <path fill="#FF0000" d="M0 0L40 0L40 40L0 40Z M10 10L30 10L30 30L10 30Z"/>
            <path fill="#0000FF" fill-rule="evenodd" transform="translate(50,0)" d="M0 0L40 0L40 40L0 40Z M10 10L30 10L30 30L10 30Z"/>
            </svg>
            """;
        var image = EasyPDF.App.Styles.SimpleSvg.Load(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(Svg)));
        Assert.Equal(new Size(100, 50), image.Size);                                // the viewBox, not just the shapes
        var shapes = ((DrawingGroup)image.Drawing!).Children.OfType<GeometryDrawing>().Skip(1).ToList();
        Assert.Equal(Colors.Red, ((ISolidColorBrush)shapes[0].Brush!).Color);
        Assert.True(shapes[0].Geometry!.FillContains(new Point(20, 20)));            // non-zero like SVG: inner square filled
        Assert.False(shapes[1].Geometry!.FillContains(new Point(70, 20)));           // even-odd: a hole ...
        Assert.True(shapes[1].Geometry!.FillContains(new Point(55, 5)));             // ... moved by the translation
    }

    [Fact]
    public void TheLicenseTextsLieNextToTheProgram()
    {
        var files = System.IO.Directory.GetFiles(VersionInfo.LicenseFolder).Select(System.IO.Path.GetFileName).ToList();
        Assert.Contains("THIRD-PARTY-NOTICES.txt", files);
        Assert.Contains("LICENSE.txt", files);       // the application's own
        Assert.Contains("Apache-2.0.txt", files);    // RapidOcrNet, PaddleOCR models, PDFiumCore
        Assert.Contains("RapidOcrNet-NOTICE.txt", files);
        var notices = System.IO.File.ReadAllText(System.IO.Path.Combine(VersionInfo.LicenseFolder, "THIRD-PARTY-NOTICES.txt"));
        foreach (var file in System.Text.RegularExpressions.Regex.Matches(notices, @"[\w.-]+\.txt").Select(m => m.Value).Distinct())
            Assert.Contains(file, files);            // every text the overview points to is there
        Assert.DoesNotContain("EasyPDF", notices);   // the shipped texts speak of "the application", no product name
        Assert.DoesNotContain(EasyPDF.Core.AppInfo.Name, notices);
    }
}

internal static class Dialogs
{
    public static string AboutWindowText() => EasyPDF.App.Dialogs.AboutWindow.BuildText();
}
