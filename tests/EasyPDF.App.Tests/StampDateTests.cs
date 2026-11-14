using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using EasyPDF.App.Controls;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Views;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Stamps;
using Xunit;
using static EasyPDF.App.Tests.StampFieldTests;

namespace EasyPDF.App.Tests;

public class StampDateTests
{
    private static async Task DoubleClick(Window window, Point p)
    {
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Pump();
    }

    private static Button DialogButton(Window dialog, bool ok) =>
        dialog.GetLogicalDescendants().OfType<Button>().First(b => ok ? b.IsDefault : b.IsCancel);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static StampOverlay OnlyStamp(MainWindow window) => Assert.Single(window.ViewModel.Document!.Pages[0].Stamps);

    /// <summary>The window point at the centre of the stamp on the first page.</summary>
    private static Point CentreOf(MainWindow window, HorizontalPageView view, StampOverlay stamp)
    {
        var origin = view.TranslatePoint(new Point(0, 0), window)!.Value;
        var pageSize = window.ViewModel.Document!.Pages[0].DisplaySize;
        for (var x = 0.0; x < view.Bounds.Width; x += 1)
            for (var y = 0.0; y < view.Bounds.Height; y += 4)
                if (view.PageIndexAt(new Point(x, y)) == 0) // the first point found from the top-left is the corner of the page
                {
                    return origin + new Point(x + stamp.Rect.CenterX * ScaleOf(view, pageSize), y + stamp.Rect.CenterY * ScaleOf(view, pageSize));
                }
        throw new InvalidOperationException("No page found.");
    }

    private static double ScaleOf(HorizontalPageView view, EasyPDF.Core.Pdfium.PageSizePt pageSize)
    {
        var y = view.Bounds.Height / 2;
        double? left = null, right = null;
        for (var x = 0.0; x < view.Bounds.Width; x += 0.5)
        {
            var inside = view.PageIndexAt(new Point(x, y)) == 0;
            if (inside && left == null)
                left = x;
            if (!inside && left != null && right == null)
                right = x;
        }
        return ((right ?? view.Bounds.Width) - left!.Value) / pageSize.Width;
    }

    [AvaloniaFact]
    public async Task ADoubleClickChangesTheDateAsOneUndoStepAndKeepsThePlace()
    {
        using var dir = new TempDir();
        var (window, view, path) = await OpenAsync(dir);
        var vm = window.ViewModel;
        await PlaceAsync(window, view, path);
        var before = OnlyStamp(window);
        Assert.Contains(before.Template!.Context.Now.ToString("d"), vm.StatusMessage);

        await DoubleClick(window, CentreOf(window, view, before));

        var dialog = Assert.Single(window.OwnedWindows.OfType<StampDateDialog>());
        Assert.Equal(DateTime.Today, dialog.Calendar.SelectedDate);
        var chosen = new DateTime(2019, 1, 11);
        dialog.Calendar.SelectedDate = chosen;
        Click(DialogButton(dialog, ok: true));
        await Pump();

        var after = OnlyStamp(window);
        Assert.NotEqual(before.Id, after.Id);
        Assert.Equal(before.Rect, after.Rect);
        Assert.Equal(chosen, after.Template!.Context.Now.Date);
        Assert.NotEqual(before.Image, after.Image);
        Assert.NotEqual(before.Bgra, after.Bgra);
        Assert.Equal(after.Id, view.SelectedStampId);
        Assert.Contains(chosen.ToString("d"), vm.StatusMessage);

        // Undo brings the old date back, and redo the new one
        vm.UndoCommand.Execute(null);
        await Pump();
        Assert.Equal(before.Id, OnlyStamp(window).Id);
        vm.RedoCommand.Execute(null);
        await Pump();
        Assert.Equal(after.Id, OnlyStamp(window).Id);
        // One step only: the next undo takes the changed stamp to the placed one, then there is no stamp
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        await Pump();
        Assert.Empty(vm.Document!.Pages[0].Stamps);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CancellingOrKeepingTheDateChangesNothing()
    {
        using var dir = new TempDir();
        var (window, view, path) = await OpenAsync(dir);
        await PlaceAsync(window, view, path);
        var before = OnlyStamp(window);

        await DoubleClick(window, CentreOf(window, view, before));
        var dialog = Assert.Single(window.OwnedWindows.OfType<StampDateDialog>());
        dialog.Calendar.SelectedDate = new DateTime(2019, 1, 11);
        Click(DialogButton(dialog, ok: false));
        await Pump();
        Assert.Same(before, OnlyStamp(window));

        await Pump(600); // the next clicks are no third and fourth click of the same double click
        await DoubleClick(window, CentreOf(window, view, before));
        dialog = Assert.Single(window.OwnedWindows.OfType<StampDateDialog>());
        Click(DialogButton(dialog, ok: true));
        await Pump();
        Assert.Same(before, OnlyStamp(window));

        await Pump(600);
        await DoubleClick(window, CentreOf(window, view, before));
        dialog = Assert.Single(window.OwnedWindows.OfType<StampDateDialog>());
        dialog.Close(); // [X]
        await Pump();
        Assert.Same(before, OnlyStamp(window));
        window.Close();
    }

    [AvaloniaFact]
    public async Task APlainPictureAndAStampWithoutADateOpenNoDialog()
    {
        using var dir = new TempDir();
        var (window, view, path) = await OpenAsync(dir);
        File.WriteAllBytes(path, Picture());
        await PlaceAsync(window, view, path);
        await DoubleClick(window, CentreOf(window, view, OnlyStamp(window)));
        Assert.Empty(window.OwnedWindows);
        window.ViewModel.UndoCommand.Execute(null);
        await Pump();

        var noDate = new StampDefinition { Fields = [RedDate.Fields[0] with { Text = "{$USER}" }] };
        File.WriteAllBytes(path, StampFile.WriteDefinition(Picture(), noDate));
        await PlaceAsync(window, view, path);
        Assert.Equal(EasyPDF.App.Localization.Loc.T("StampPlaced"), window.ViewModel.StatusMessage);
        await Pump(600);
        await DoubleClick(window, CentreOf(window, view, OnlyStamp(window)));
        Assert.Empty(window.OwnedWindows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheChangedStampIsDrawnWithItsNewPicture()
    {
        using var dir = new TempDir();
        var (window, view, path) = await OpenAsync(dir);
        await PlaceAsync(window, view, path);
        var before = OnlyStamp(window);
        var p = CentreOf(window, view, before);

        await DoubleClick(window, p);
        var dialog = Assert.Single(window.OwnedWindows.OfType<StampDateDialog>());
        dialog.Calendar.SelectedDate = new DateTime(2019, 1, 11);
        Click(DialogButton(dialog, ok: true));
        await Pump();
        var after = OnlyStamp(window);

        // The ink differs between the two dates (other digits), and both are complete pictures
        Assert.NotEqual(RedInk(before.Bgra!, before.PixelWidth, before.PixelHeight).Count, RedInk(after.Bgra!, after.PixelWidth, after.PixelHeight).Count);
        Assert.NotNull(window.CaptureRenderedFrame());
        window.Close();
    }
}
