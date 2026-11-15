using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using EasyPDF.App.Controls;
using EasyPDF.App.Dialogs;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Templates;
using Xunit;

namespace EasyPDF.App.Tests;

public class TemplateUiTests
{
    private static async Task Pump(int ms = 300)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task ChoosingATemplateAndClickingPlacesItWithExpandedMacros()
    {
        using var dir = new TempDir();
        var folder = System.IO.Path.Combine(dir.Path, "templates");
        System.IO.Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "Pg.txt"), "Page {$PAGE}/{$PAGES} {$FILE}\r\n{$NOPE}\r\n");
        var settings = new AppSettings { Language = "en", TemplateFolder = folder };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new EasyPDF.App.Views.MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("report.pdf", 3));
        await Pump();
        var view = window.FindControl<HorizontalPageView>("PageView")!;
        var vm = window.ViewModel;

        vm.SelectTemplateCommand.Execute(Assert.Single(vm.LoadTemplates()));
        Assert.True(vm.IsTextToolActive);
        var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
        var b = view.PageRectInViewport(0);
        var p = origin + new Point(b.X + b.Width / 4, b.Y + b.Height / 4);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();

        Assert.Empty(view.Children.OfType<TextBox>()); // no editor: placed at once
        Assert.False(vm.IsTextToolActive);
        var text = Assert.Single(vm.Document!.Pages[0].Texts);
        Assert.Equal("Page 1/3 report\n{$NOPE}", text.Text);
        Assert.True(text.Rect.Width > 20);

        // The plain text tool afterwards still opens the editor (the template is forgotten)
        vm.SelectTextToolCommand.Execute(null);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();
        Assert.Single(view.Children.OfType<TextBox>());

        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    private static async Task<(EasyPDF.App.Views.MainWindow Window, HorizontalPageView View, TempDir Dir)> OpenWithTemplateAsync(string template)
    {
        var dir = new TempDir();
        var folder = System.IO.Path.Combine(dir.Path, "templates");
        System.IO.Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "T.txt"), template);
        var settings = new AppSettings { Language = "en", TemplateFolder = folder };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new EasyPDF.App.Views.MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await window.OpenPathAsync(dir.Pdf("report.pdf", 3));
        await Pump();
        window.ViewModel.TextFontSize = 14;
        window.ViewModel.TextColor = "#FF00FF";
        return (window, window.FindControl<HorizontalPageView>("PageView")!, dir);
    }

    private static Point InPage(Window window, HorizontalPageView view, double fractionX, double fractionY)
    {
        var b = view.PageRectInViewport(0);
        return view.TranslatePoint(new Point(b.X + b.Width * fractionX, b.Y + b.Height * fractionY), window)!.Value;
    }

    /// <summary>The pixels in the colour of the text inside the reading view (the ribbon shows the pen colour too), in window coordinates.</summary>
    private static unsafe System.Collections.Generic.HashSet<(int X, int Y)> TextPixels(Window window, HorizontalPageView view)
    {
        var area = new Rect(view.TranslatePoint(new Point(0, 0), window)!.Value, view.Bounds.Size);
        using var frame = window.CaptureRenderedFrame()!;
        using var fb = frame.Lock();
        var bgra = fb.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        var found = new System.Collections.Generic.HashSet<(int X, int Y)>();
        for (var y = 0; y < fb.Size.Height; y++)
        {
            var row = (byte*)fb.Address + y * fb.RowBytes;
            for (var x = 0; x < fb.Size.Width; x++)
            {
                var p = row + x * 4;
                var (r, b) = bgra ? (p[2], p[0]) : (p[0], p[2]);
                if (r >= 200 && b >= 200 && p[1] <= 120 && area.Contains(new Point(x, y)))
                    found.Add((x, y));
            }
        }
        return found;
    }

    [AvaloniaTheory]
    [InlineData(false, 0.25)]
    [InlineData(false, 0.98)] // near the edge: pushed back onto the page, the preview too
    [InlineData(true, 0.25)]
    public async Task TheChosenTemplateFollowsThePointerAndLandsExactlyWhereItWasShown(bool turnedPage, double fractionX)
    {
        var (window, view, dir) = await OpenWithTemplateAsync("Page {$PAGE}\nof {$PAGES}");
        using (dir)
        {
            var vm = window.ViewModel;
            if (turnedPage)
            {
                vm.Document!.Rotate([0], 90);
                await Pump();
            }

            vm.SelectTemplateCommand.Execute(Assert.Single(vm.LoadTemplates()));
            await Pump(100);
            Assert.Empty(TextPixels(window, view)); // the pointer has not moved over a page yet

            var first = InPage(window, view, 0.25, 0.25);
            window.MouseMove(first);
            await Pump(100);
            var atFirst = TextPixels(window, view);
            Assert.True(atFirst.Count > 50, $"preview pixels: {atFirst.Count}");
            Assert.InRange(atFirst.Min(q => q.X) - first.X, 0, 6); // the top-left corner of the text is at the pointer
            Assert.InRange(atFirst.Min(q => q.Y) - first.Y, 0, 20);

            var p = InPage(window, view, fractionX, 0.5);
            window.MouseMove(p);
            await Pump(100);
            var preview = TextPixels(window, view);
            Assert.False(preview.SetEquals(atFirst));

            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            await Pump();
            Assert.Equal("Page 1\nof 3", Assert.Single(vm.Document!.Pages[0].Texts).Text);
            Assert.True(preview.SetEquals(TextPixels(window, view))); // nothing moved, nothing doubled
            window.Close();
            await vm.ReleaseDocumentAsync();
        }
    }

    [AvaloniaFact]
    public async Task TheTextReplacesThePointerOverAPageAndEscEndsIt()
    {
        var (window, view, dir) = await OpenWithTemplateAsync("Draft");
        using (dir)
        {
            var vm = window.ViewModel;
            var surface = view.Children[0];
            var onPage = InPage(window, view, 0.5, 0.5);

            vm.SelectTemplateCommand.Execute(Assert.Single(vm.LoadTemplates()));
            window.MouseMove(onPage);
            await Pump(100);
            Assert.Equal("None", surface.Cursor?.ToString());
            Assert.NotEmpty(TextPixels(window, view));

            // Beside the pages there is no text to show and no place to put it: the pointer comes back
            var b = view.PageRectInViewport(0);
            var besidePage = view.TranslatePoint(new Point(b.X + b.Width / 2, b.Y - 2), window)!.Value;
            window.MouseMove(besidePage);
            await Pump(100);
            Assert.Equal("Ibeam", surface.Cursor?.ToString());
            Assert.Empty(TextPixels(window, view));

            window.MouseMove(onPage);
            await Pump(100);
            Assert.NotEmpty(TextPixels(window, view));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Pump(100);
            Assert.False(vm.IsTextToolActive);
            Assert.Null(surface.Cursor);
            Assert.Empty(TextPixels(window, view));
            Assert.Empty(vm.Document!.Pages[0].Texts);

            // The plain text tool has nothing to preview: it keeps its pointer
            vm.SelectTextToolCommand.Execute(null);
            window.MouseMove(onPage + new Point(5, 5));
            await Pump(100);
            Assert.Equal("Ibeam", surface.Cursor?.ToString());
            Assert.Empty(TextPixels(window, view));
            window.Close();
            await vm.ReleaseDocumentAsync();
        }
    }

    [AvaloniaFact]
    public void TheDialogEditsACopyAndReturnsTheNewList()
    {
        var original = new[] { new TextTemplate { Name = "A", Text = "x" } };
        var dialog = new TemplatesDialog(original);
        var names = dialog.GetLogicalDescendants().OfType<TextBox>().ToList();
        Assert.Equal("A", names[0].Text);
        names[0].Text = "B";
        Assert.Equal("A", original[0].Name); // the dialog works on a copy
    }

    [AvaloniaFact]
    public async Task TheStartCreatesTheFolderWithSamplesInTheProgramLanguageAndTheFlyoutListsThem()
    {
        using var dir = new TempDir();
        var folder = System.IO.Path.Combine(dir.Path, "Vorlagen");
        var settings = new AppSettings { Language = "de", TemplateFolder = folder, StampFolder = System.IO.Path.Combine(dir.Path, "stamps") };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        try
        {
            // What App does at program start
            Assert.True(EasyPDF.App.Services.SampleContent.Provide(settings));
            Assert.Contains("template:Page", settings.ProvidedSamples);
            Assert.Contains("stamp:Stempel 'gescannt'.png", settings.ProvidedSamples);
            Assert.False(EasyPDF.App.Services.SampleContent.Provide(settings)); // nothing new the second time

            var window = new EasyPDF.App.Views.MainWindow(settings) { Width = 1000, Height = 700 };
            window.Show();
            await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
            await Pump();
            var button = window.FindControl<EasyPDF.App.Controls.Ribbon.RibbonButton>("TemplatesButton")!;
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            flyout.ShowAt(button);
            await Pump();

            var sample = System.IO.Path.Combine(folder, "Seite x von y.txt");
            Assert.Equal("Seite {$PAGE} von {$PAGES}", System.IO.File.ReadAllText(sample));
            var host = (StackPanel)((ScrollViewer)flyout.Content!).Content!;
            var names = host.Children.OfType<Button>().Select(b => (string)b.Content!).ToArray();
            Assert.Equal(["Datum", "Eingegangen", "Entwurf", "Seite x von y", EasyPDF.App.Localization.Loc.T("TemplatesEdit"), EasyPDF.App.Localization.Loc.T("TemplateOpenFolder")], names);
            flyout.Hide();

            // What the Templates dialog returns is written back as files: a renamed template replaces its file
            var before = window.ViewModel.LoadTemplates();
            var after = before.Select(t => new TextTemplate { Name = t.Name == "Entwurf" ? "Vorläufig" : t.Name, Text = t.Text, FilePath = t.FilePath }).ToList();
            await window.ViewModel.SaveTemplatesAsync(before, after);
            Assert.Equal(["Datum", "Eingegangen", "Seite x von y", "Vorläufig"], window.ViewModel.LoadTemplates().Select(t => t.Name).ToArray());
            window.Close();
        }
        finally
        {
            EasyPDF.App.Services.AppEnvironment.ApplyLanguage("en");
        }
    }

    private static async Task Click(Window window, Control target)
    {
        var p = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump(100);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewTemplateTypedInTheDialogIsSavedAsAFile(bool clickIntoTheText)
    {
        using var dir = new TempDir();
        var folder = System.IO.Path.Combine(dir.Path, "templates");
        var settings = new AppSettings { Language = "en", TemplateFolder = folder };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new EasyPDF.App.Views.MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await Pump(100);

        var edit = window.EditTemplatesAsync();
        await Pump();
        var dialog = Assert.IsType<TemplatesDialog>(window.OwnedWindows.Single());
        Button ButtonNamed(string key) => dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == EasyPDF.App.Localization.Loc.T(key));
        var boxes = dialog.GetLogicalDescendants().OfType<TextBox>().ToList();

        await Click(dialog, ButtonNamed("TemplateAdd"));
        dialog.KeyTextInput("Greeting"); // the name box has the focus, its text is marked
        await Pump(100);
        if (clickIntoTheText)
            await Click(dialog, boxes[1]);
        else
            dialog.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        await Pump(100);
        dialog.KeyTextInput("Hello");
        await Pump(100);
        Assert.Equal("Greeting", boxes[0].Text);
        Assert.Equal("Hello", boxes[1].Text);
        await Click(dialog, ButtonNamed("Ok"));
        await edit;

        var saved = Assert.Single(TemplateLibrary.Load(folder));
        Assert.Equal("Greeting", saved.Name);
        Assert.Equal("Hello", saved.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AFolderThatCantBeUsedIsReportedInsteadOfNothingHappening()
    {
        using var dir = new TempDir();
        System.IO.File.WriteAllText(dir.File("blocked"), "a file where the folder should be");
        var folder = System.IO.Path.Combine(dir.Path, "blocked", "templates");
        var settings = new AppSettings { Language = "en", TemplateFolder = folder };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new EasyPDF.App.Views.MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        await Pump(100);

        var open = EasyPDF.App.Services.FolderLauncher.OpenAsync(window, folder);
        await Pump();
        var message = Assert.IsType<MessageDialog>(window.OwnedWindows.Single());
        Assert.Contains(message.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == EasyPDF.App.Localization.Loc.F("FolderOpenFailed", folder));
        message.Close();
        await open;

        var dialogs = new FakeDialogs();
        var vm = new EasyPDF.App.ViewModels.MainViewModel(settings, dialogs);
        await vm.SaveTemplatesAsync([], [new TextTemplate { Name = "A", Text = "x" }]);
        Assert.Contains(EasyPDF.App.Localization.Loc.F("TemplatesSaveFailed", folder), dialogs.Errors);
        window.Close();
    }
}
