using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Views;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Quick guide: F1 / Help menu / ribbon / start page open one non-modal window with a section per function.</summary>
public class QuickGuideTests
{
    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static Color Resource(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, control.ActualThemeVariant, out var value));
        return ((ISolidColorBrush)value!).Color;
    }

    [AvaloniaFact]
    public async Task F1OpensTheGuideOnceWithEveryTopicAndItsIcon()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        window.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        await Pump();
        var guide = Assert.Single(window.OwnedWindows.OfType<QuickGuideWindow>());
        Assert.True(window.IsEnabled);                                                  // not modal: the document stays usable

        var rows = guide.GetVisualDescendants().OfType<Grid>().Where(g => g.Tag is string).ToList();
        Assert.Equal(QuickGuideWindow.Topics.Select(t => t.Key), rows.Select(r => (string)r.Tag!));
        foreach (var (row, topic) in rows.Zip(QuickGuideWindow.Topics))
        {
            var icon = row.Children.OfType<RibbonButton>().Single();
            Assert.NotNull(icon.Icon);                                                  // the icon key exists
            Assert.Equal(topic.Emphasis == null, icon.IconColor != null);
            // The round icon of the ribbon: a circle, filled in the primary colour for Open and accent for Save like there
            var circle = icon.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("iconCircle"));
            Assert.Equal(44, circle.Bounds.Width);
            var fill = ((ISolidColorBrush)circle.Background!).Color;
            if (topic.Key == "Open")
                Assert.Equal(Resource(guide, "Circle.Active"), fill);
            else if (topic.Key == "Save")
                Assert.Equal(EasyPDF.App.Styles.BrandColors.Accent, fill);
            else
                Assert.Equal(Resource(guide, "Circle.Background"), fill);
            var texts = row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();
            Assert.Equal(2, texts.Count);                                               // title and text
            Assert.All(texts, t => Assert.DoesNotContain("!", t[..1]));                 // "!Key!" = missing resource
            Assert.DoesNotContain("{0}", texts[1]);
        }

        // A second F1 brings the same window to the front instead of opening another
        window.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        await Pump();
        Assert.Single(window.OwnedWindows.OfType<QuickGuideWindow>());

        // Esc closes it
        guide.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump();
        Assert.Empty(window.OwnedWindows.OfType<QuickGuideWindow>());
        window.Close();
    }

    [AvaloniaFact]
    public void TheRibbonAndTheStartPageOfferTheGuide()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        Assert.Same(window.ViewModel.ShowQuickGuideCommand, window.FindControl<RibbonButton>("GuideButton")!.Command);
        var start = window.FindControl<Button>("StartGuideButton")!;
        Assert.Same(window.ViewModel.ShowQuickGuideCommand, start.Command);
        Assert.True(start.IsEffectivelyVisible);                                        // no document: the start page shows it
        window.Close();
    }
}
