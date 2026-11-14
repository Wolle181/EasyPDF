using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class SaveWithViewTests
{
    /// <summary>Saving does not raise the document's change event on the worker thread, where it would touch the open views ("different thread owns it").</summary>
    [AvaloniaFact]
    public async Task SavingWithOpenViewsTellsTheViewsOnTheUiThread()
    {
        var settings = new AppSettings { Language = "en" };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new EasyPDF.App.Views.MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        var path = dir.Pdf("a.pdf", 2);
        await window.OpenPathAsync(path);
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        var vm = window.ViewModel;

        vm.Document!.AddText(0, "Hello", "Helvetica", 12, "#000000", new RectPt(10, 10, 30, 14));
        await vm.SaveCommand.ExecuteAsync(null);
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }

        Assert.False(vm.Document.IsDirty);
        Assert.Empty(vm.Document.Pages[0].Texts); // written into the file now
        window.Close();
        await vm.Document.DisposeAsync();
    }

    /// <summary>After opening, landscape pages far behind portrait first pages are shown in landscape, not distorted.</summary>
    [AvaloniaTheory]
    [InlineData(40)]
    [InlineData(700)]   // several batches of page sizes
    public async Task LandscapePagesFarBackAreLandscapeAfterOpening(int pageCount)
    {
        var settings = new AppSettings { Language = "en" };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new EasyPDF.App.Views.MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        var path = dir.Pdf("long.pdf", pageCount);              // width 100 + 10·i, height 200: from page 12 on landscape
        await window.OpenPathAsync(path);
        for (var i = 0; i < 25; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        var view = window.FindControl<EasyPDF.App.Controls.HorizontalPageView>("PageView")!;
        var last = pageCount - 1;
        view.ScrollToPage(last);
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        var rect = view.PageRectInViewport(last);
        Assert.Equal((100 + 10.0 * last) / 200, rect.Width / rect.Height, 2);
        window.Close();
        await window.ViewModel.Document!.DisposeAsync();
    }

    /// <summary>After saving, landscape pages behind a portrait first page are shown in landscape, not distorted.</summary>
    [AvaloniaFact]
    public async Task LandscapePagesStayLandscapeAfterOpeningAndSaving()
    {
        var settings = new AppSettings { Language = "en" };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new EasyPDF.App.Views.MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        var path = dir.Pdf("mixed.pdf", 5, widthBase: 80);   // widths 180 … 220, height 200: pages 4 and 5 are landscape
        await window.OpenPathAsync(path);
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        var vm = window.ViewModel;
        var view = window.FindControl<EasyPDF.App.Controls.HorizontalPageView>("PageView")!;
        double Aspect(int page) => view.PageRectInViewport(page).Width / view.PageRectInViewport(page).Height;
        Assert.Equal(1.1, Aspect(4), 2);

        vm.Document!.Move([1], 0);                              // first page still portrait
        await vm.SaveCommand.ExecuteAsync(null);
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.False(vm.Document.IsDirty);
        Assert.Equal(0.95, Aspect(0), 2);                       // 190 × 200
        Assert.Equal(1.1, Aspect(4), 2);
        window.Close();
        await vm.Document.DisposeAsync();
    }
}
