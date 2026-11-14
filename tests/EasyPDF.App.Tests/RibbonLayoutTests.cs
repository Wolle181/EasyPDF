using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Narrow windows: the ribbon groups shrink step by step like in Office instead of being cut off.</summary>
public class RibbonLayoutTests
{
    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static Point CenterOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>Reduction levels of the groups of the shown tab, and whether the last group ends inside the window.</summary>
    private static (int[] Levels, bool Fits) Measure(Window window)
    {
        var groups = window.GetVisualDescendants().OfType<RibbonGroup>().ToList();
        var right = groups.Last().TranslatePoint(new Point(groups.Last().Bounds.Width, 0), window)!.Value.X;
        return ([.. groups.Select(g => g.Reduction)], right <= window.Bounds.Width + 0.5);
    }

    [AvaloniaFact]
    public async Task GroupsShrinkInNarrowWindowsAndGrowBackInWideOnes()
    {
        var window = new MainWindow(new AppSettings()) { Width = 1600, Height = 400 };
        window.Show();
        var ribbon = window.GetVisualDescendants().OfType<Ribbon>().Single();
        for (var tab = 0; tab < ribbon.Items.Count; tab++)
        {
            ribbon.SelectedIndex = tab;
            window.Width = 1600;
            await Pump();
            var wide = Measure(window);
            Assert.True(wide.Fits);
            Assert.All(wide.Levels, l => Assert.Equal(0, l));                       // room for everything: as designed

            window.Width = 700;                                                    // the window's minimum width
            await Pump();
            var narrow = Measure(window);
            Assert.True(narrow.Fits, $"Tab {tab} is cut off at 700 px");
            Assert.Contains(narrow.Levels, l => l > 0);
            Assert.True(narrow.Levels[^1] >= narrow.Levels[0]);                    // the right groups shrink first
        }

        // Back to wide: everything as designed again; the icon-only buttons kept their label in the tooltip
        ribbon.SelectedIndex = 0;
        window.Width = 700;
        await Pump();
        var iconOnly = window.GetVisualDescendants().OfType<RibbonButton>().First(b => b.Reduction == 2 && b.Command == window.ViewModel.UndoCommand);
        Assert.StartsWith("Undo", ToolTip.GetTip(iconOnly) as string);
        window.Width = 1600;
        await Pump();
        Assert.All(Measure(window).Levels, l => Assert.Equal(0, l));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ALanguageWithLongerTextsStillFits()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            Loc.SetCulture(new CultureInfo("en"));
            var window = new MainWindow(new AppSettings()) { Width = 760, Height = 400 };
            window.Show();
            await Pump();
            Assert.True(Measure(window).Fits, string.Join(" ", Measure(window).Levels));
            AppEnvironment.ApplyLanguage("de");                                    // German labels are longer
            window.ViewModel.RefreshTexts();
            await Pump();
            Assert.True(Measure(window).Fits, string.Join(" ", Measure(window).Levels));
            window.Close();
        }
        finally
        {
            Loc.SetCulture(old);
        }
    }

    [AvaloniaFact]
    public async Task ACollapsedGroupOpensItsButtonsBelowAndWorksFromThere()
    {
        var clicked = 0;
        var command = new RelayCommand(() => clicked++);
        var panel = new RibbonGroupPanel();
        for (var i = 0; i < 9; i++)                                                // three columns of small buttons
            panel.Children.Add(new RibbonButton { Label = $"Button {i}", Icon = Geometry(), Command = command });
        var group = new RibbonGroup { Caption = "Tools", Content = panel };
        var window = new Window { Width = 90, Height = 200, Content = new RibbonGroupsPanel { Height = 106, Children = { group } } };
        window.Show();
        await Pump();

        var button = group.CollapsedButton;
        Assert.NotNull(button);                                                    // too narrow even for icons only
        Assert.Equal("Tools ▾", button.Label);
        Assert.Null(panel.Parent is RibbonGroup ? panel : null);                   // the buttons left the ribbon...
        window.MouseDown(CenterOf(button, window), MouseButton.Left);
        window.MouseUp(CenterOf(button, window), MouseButton.Left);
        await Pump();
        var flyout = (Flyout)button.Flyout!;
        Assert.True(flyout.IsOpen);                                                // ...and appear below it, as designed
        var inside = panel.Children.OfType<RibbonButton>().First();
        Assert.Equal(0, inside.Reduction);
        Assert.True(inside.IsAttachedToVisualTree());
        Assert.Same(command, inside.Command);
        inside.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Pump();
        Assert.False(flyout.IsOpen);                                               // choosing a command closes it

        window.Width = 600;
        await Pump();
        Assert.Null(group.CollapsedButton);
        Assert.Same(panel, group.Content);
        Assert.Equal(0, group.Reduction);
        window.Close();
    }

    private static Avalonia.Media.Geometry Geometry() => Avalonia.Media.Geometry.Parse("M4,4 L20,20");
}
