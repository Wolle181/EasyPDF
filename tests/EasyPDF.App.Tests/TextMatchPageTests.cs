using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Controls;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Fonts;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Option "Like the text on the page": new text takes the font and size most of the page's text is written in.</summary>
public class TextMatchPageTests
{
    private static async Task Pump(int ms = 300)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    /// <summary>A PDF whose first page is written in Times bold 14 (a short line in Helvetica 8 does not count); the second page has no text.</summary>
    private static async Task<string> TimesPdfAsync(TempDir dir)
    {
        var source = await PdfSource.OpenAsync(dir.Pdf("plain.pdf", 2));
        await source.LoadPageSizesAsync();
        await using var document = EditableDocument.FromSource(source);
        document.AddText(0, "The body text of this page is set in Times bold", "Times", 14, "#000000", new RectPt(40, 60, 400, 20), bold: true);
        document.AddText(0, "note", "Helvetica", 8, "#000000", new RectPt(40, 100, 30, 10));
        var path = dir.File("times.pdf");
        await document.SaveAsync(path);
        return path;
    }

    [Fact]
    public async Task WithTheOptionNewTextTakesThePagesFontAndSizeOtherwiseTheDefault()
    {
        using var dir = new TempDir();
        var settings = new AppSettings { TextFont = "Courier", TextFontSize = 10 };
        var vm = new MainViewModel(settings, new FakeDialogs());
        await vm.OpenPathAsync(await TimesPdfAsync(dir));
        var defaultPen = new TextPen("Courier", 10);

        Assert.Equal(defaultPen, vm.PenFor(0));                 // option off
        vm.TextMatchPage = true;
        Assert.True(settings.TextMatchPage);
        Assert.Equal(defaultPen, vm.PenFor(0));                 // read in the background: the default until then
        var page = vm.Document!.Pages[0];
        await vm.LoadPagePenAsync((page.Source, page.SourceIndex));
        Assert.Equal(new TextPen("Times", 14, true), vm.PenFor(0));
        await vm.LoadPagePenAsync((vm.Document.Pages[1].Source, 1));
        Assert.Equal(defaultPen, vm.PenFor(1));                 // a page without text: the default

        vm.PlaceText(0, new RectPt(40, 200, 80, 18), "Added", vm.PenFor(0));
        var added = vm.Document.Pages[0].Texts.Last();
        Assert.Equal(("Times", 14.0, true, false), (added.Font, added.FontSize, added.Bold, added.Italic));

        vm.TextMatchPage = false;
        Assert.Equal(defaultPen, vm.PenFor(0));
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task TheEditorWritesInThePagesFontAndTheTextKeepsIt()
    {
        var settings = new AppSettings { Language = "en", TextFont = "Helvetica", TextFontSize = 10, TextMatchPage = true };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(await TimesPdfAsync(dir));
        await Pump();
        var view = window.FindControl<HorizontalPageView>("PageView")!;
        var vm = window.ViewModel;
        var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
        var page = view.PageRectInViewport(0);

        vm.SelectTextToolCommand.Execute(null);                  // starts reading the page's font
        await Pump(600);
        var at = origin + new Point(page.X + page.Width * 0.2, page.Y + page.Height * 0.5);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        await Pump();
        var editor = view.Children.OfType<TextBox>().Single();
        Assert.Equal(Avalonia.Media.FontWeight.Bold, editor.FontWeight);
        Assert.Contains("Times", editor.FontFamily.ToString());
        editor.Text = "Matched";
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Pump();

        var text = vm.Document!.Pages[0].Texts.Last();
        Assert.Equal(("Matched", "Times", 14.0, true), (text.Text, text.Font, text.FontSize, text.Bold));
        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task ThePenDropdownSwitchesTheOption()
    {
        var settings = new AppSettings { Language = "en" };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 1));
        await Pump();
        var flyout = Assert.IsType<Flyout>(window.FindControl<EasyPDF.App.Controls.Ribbon.RibbonButton>("TextStyleButton")!.Flyout);
        var box = ((StackPanel)flyout.Content!).Children.OfType<CheckBox>().Single(c => c.Name == "TextMatchPageBox");
        Assert.False(box.IsChecked);
        box.IsChecked = true;
        Assert.True(window.ViewModel.TextMatchPage);
        Assert.True(settings.TextMatchPage);
        window.Close();
        await window.ViewModel.ReleaseDocumentAsync();
    }
}
