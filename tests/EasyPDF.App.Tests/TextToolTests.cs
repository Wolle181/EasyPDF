using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Controls;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class TextToolTests
{
    [AvaloniaFact]
    public async Task PenColourFontAndSizeAreRememberedInTheSettings()
    {
        var settings = new AppSettings { Language = "en" };
        EasyPDF.App.Services.AppEnvironment.Apply(settings);
        var window = new MainWindow(settings) { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        for (var i = 0; i < 15; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        var vm = window.ViewModel;

        Assert.Equal("#000000", vm.TextColor);
        vm.TextColor = "c00000";
        vm.TextFont = "times";
        vm.TextFontSize = 9999;
        Assert.Equal("#C00000", settings.TextColor);
        Assert.Equal("Times", settings.TextFont);
        Assert.Equal(TextStyle.MaxSize, settings.TextFontSize);
        vm.TextColor = "nonsense";
        Assert.Equal("#000000", vm.TextColor);

        // The next session starts with the remembered style
        var next = new EasyPDF.App.ViewModels.MainViewModel(settings, window);
        Assert.Equal("Times", next.TextFont);

        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task TextToolWaitsForAClickAndEscapeEndsIt()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        for (var i = 0; i < 15; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        var vm = window.ViewModel;

        vm.SelectTextToolCommand.Execute(null);
        Assert.True(vm.IsTextToolActive);
        Assert.True(vm.CancelStampCommand.CanExecute(null));
        vm.CancelStampCommand.Execute(null);
        Assert.False(vm.IsTextToolActive);

        window.Close();
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task AClickOfTheTextToolUsesItUpEvenBeforeTheStyleWasChanged()
    {
        // The window listens to "editor opened" from the start, not only after a change of the pen style (and only once)
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        for (var i = 0; i < 15; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20, TestContext.Current.CancellationToken); }
        var vm = window.ViewModel;
        var view = window.FindControl<HorizontalPageView>("PageView")!;
        int Handlers() => (typeof(HorizontalPageView).GetField(nameof(HorizontalPageView.TextEditorOpened), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view) as Delegate)?.GetInvocationList().Length ?? 0;
        Assert.Equal(1, Handlers());

        Point OverFirstPage()
        {
            var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
            for (var x = 1.0; x < view.Bounds.Width; x += 3)
                if (view.PageIndexAt(new Point(x, view.Bounds.Height / 2)) == 0)
                    return new Point(origin.X + x + 20, origin.Y + view.Bounds.Height / 2);
            throw new Xunit.Sdk.XunitException("page 1 is not visible");
        }

        vm.SelectTextToolCommand.Execute(null);
        var p = OverFirstPage();
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Assert.False(vm.IsTextToolActive);              // the editor is open, the tool is used up

        vm.TextColor = "#C00000";
        vm.TextFont = "Times";
        vm.TextFontSize = 18;
        Assert.Equal(1, Handlers());                    // style changes do not add handlers
        Assert.Equal("#C00000", view.PenColor);         // but reach the view
        Assert.Equal("Times", view.PenFont);
        Assert.Equal(18, view.PenSize);

        window.Close();
        await vm.ReleaseDocumentAsync();
    }
}
