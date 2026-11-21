using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Services;
using EasyPDF.App.Views;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>Files dropped from outside into the Concatenate dialog are added to its list (desktop window and web overlay).</summary>
public class ConcatenateDropTests
{
    private static string DemoPdf()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "EasyPDF.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "demo-pdfs", "Annual Report 2025.pdf");
    }

    private static async Task Pump(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static async Task DropAsync(TopLevel top, Point point, string path)
    {
        var file = await top.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        Assert.NotNull(file);
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(file));
        var window = (Window)top;
        window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        await Pump(1000);
    }

    [AvaloniaFact]
    public async Task AFileDroppedOnTheDialogWindowIsAdded()
    {
        var dialog = new ConcatenateDialog(new ConcatenateContext(true), 3) { Width = 700, Height = 460 };
        dialog.Show();
        await Pump();
        var view = dialog.View;
        await DropAsync(dialog, new Point(350, 150), DemoPdf());
        Assert.Contains(view.Model.Items, i => i.Path == DemoPdf());
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task AFileDroppedOnTheDialogOverlayIsAddedAndNotOpenedBehindIt()
    {
        var window = new MainWindow { Width = 1200, Height = 800 };
        window.Show();
        await Pump();
        var layer = window.FindControl<DialogLayer>("Dialogs")!;
        var view = new ConcatenateDialogView(new ConcatenateContext(false), 0);
        _ = layer.ShowAsync(view);
        await Pump();
        var center = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
        await DropAsync(window, center, DemoPdf());
        Assert.Contains(view.Model.Items, i => i.Path == DemoPdf());
        Assert.False(window.ViewModel.HasDocument);
        window.Close();
    }
}
