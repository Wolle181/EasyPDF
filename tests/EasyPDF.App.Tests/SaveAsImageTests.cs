using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>"Save as image": resolution/colour dialog, save under a new name, the pages hold no text any more.</summary>
public class SaveAsImageTests
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

    private static async Task<int> WordCountAsync(string path)
    {
        await using var source = await PdfSource.OpenAsync(path);
        return (await source.ReadWordsAsync(0)).Count;
    }

    /// <summary>A one-page PDF with a line of text (text.pdf).</summary>
    private static async Task<string> TextPdfAsync(TempDir dir)
    {
        var source = await PdfSource.OpenAsync(dir.Pdf("plain.pdf", 1));
        await source.LoadPageSizesAsync();
        await using var document = EditableDocument.FromSource(source);
        document.AddText(0, "Text that must not stay text", "Helvetica", 14, "#000000", new RectPt(40, 60, 300, 20));
        var path = dir.File("text.pdf");
        await document.SaveAsync(path);
        return path;
    }

    [Fact]
    public async Task SavingAsImageWritesACopyWithoutTextAndShowsIt()
    {
        using var dir = new TempDir();
        var original = await TextPdfAsync(dir);
        var before = File.ReadAllBytes(original);
        var dialogs = new FakeDialogs { RasterAnswer = new PageRasterization(150, true), SavePath = dir.File("fixed.pdf") };
        var vm = new MainViewModel(new AppSettings(), dialogs);
        await vm.OpenPathAsync(original);
        Assert.True(await WordCountAsync(original) > 0);

        await vm.SaveAsImageCommand.ExecuteAsync(null);
        Assert.Equal(PageRasterization.Default, dialogs.LastRasterPreset);        // 200 dpi, colour
        Assert.Equal(Loc.F("RasterFileName", "text"), dialogs.SuggestedFileName);
        Assert.Equal(dir.File("fixed.pdf"), vm.Document!.FilePath);
        Assert.False(vm.IsDirty);
        Assert.Equal(before, File.ReadAllBytes(original));                        // the original stays as it is
        Assert.Equal(0, await WordCountAsync(dir.File("fixed.pdf")));
        Assert.Empty(dialogs.Errors);

        // The choice made last is offered next time; cancelling writes nothing
        dialogs.RasterAnswer = null;
        dialogs.SavePath = dir.File("never.pdf");
        await vm.SaveAsImageCommand.ExecuteAsync(null);
        Assert.Equal(new PageRasterization(150, true), dialogs.LastRasterPreset);
        Assert.False(File.Exists(dir.File("never.pdf")));
        await vm.Document!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TheDialogPreselectsTheLastChoiceAndReturnsTheNewOne()
    {
        var owner = new Window { Width = 600, Height = 600 };
        owner.Show();

        var dialog = new SaveAsImageDialog(PageRasterization.Default);
        var result = dialog.ShowDialog<PageRasterization?>(owner);
        await Pump();
        Assert.True(dialog.FindControl<RadioButton>("Dpi200Option")!.IsChecked);
        Assert.False(dialog.FindControl<CheckBox>("GrayscaleBox")!.IsChecked);
        foreach (var name in new[] { "Dpi300Option", "GrayscaleBox", "OkButton" })
        {
            var control = dialog.FindControl<Control>(name)!;
            dialog.MouseDown(CenterOf(control, dialog), MouseButton.Left);
            dialog.MouseUp(CenterOf(control, dialog), MouseButton.Left);
            await Pump(60);
        }
        Assert.Equal(new PageRasterization(300, true), await result);

        var closed = new SaveAsImageDialog(new PageRasterization(150, true));
        var none = closed.ShowDialog<PageRasterization?>(owner);
        await Pump();
        Assert.True(closed.FindControl<RadioButton>("Dpi150Option")!.IsChecked);
        Assert.True(closed.FindControl<CheckBox>("GrayscaleBox")!.IsChecked);
        closed.Close(); // [X] = cancel
        Assert.Null(await none);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TheRibbonAndTheMenuOfferTheCommandOnceADocumentIsOpen()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        Assert.Contains(window.LogicalDescendantsOfType<RibbonButton>(), b => b.Command == window.ViewModel.SaveAsImageCommand);
        Assert.False(window.ViewModel.SaveAsImageCommand.CanExecute(null));

        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        await Pump();
        Assert.True(window.ViewModel.SaveAsImageCommand.CanExecute(null));
        window.Close();
        await window.ViewModel.ReleaseDocumentAsync();
    }
}
