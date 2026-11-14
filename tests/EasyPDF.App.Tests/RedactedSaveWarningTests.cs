using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>
/// Saving blacked-out pages over the original file removes the covered content for good, so the program asks first
/// (save a copy, overwrite, cancel), unless the warning is switched off in the settings.
/// </summary>
public class RedactedSaveWarningTests
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

    private static async Task<(MainViewModel Vm, FakeDialogs Dialogs, AppSettings Settings, string Path)> OpenRedactedAsync(TempDir dir)
    {
        var settings = new AppSettings();
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(settings, dialogs);
        var path = dir.Pdf("a.pdf", 2);
        await vm.OpenPathAsync(path);
        vm.Document!.AddRedactions([(0, new RectPt(10, 10, 30, 20))], RedactionFill.Black);
        return (vm, dialogs, settings, path);
    }

    [AvaloniaFact]
    public async Task CancelLeavesTheFileAndTheBoxesAsTheyAre()
    {
        using var dir = new TempDir();
        var (vm, dialogs, _, path) = await OpenRedactedAsync(dir);
        var before = File.ReadAllBytes(path);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.RedactedAsked);
        Assert.Equal(("a.pdf", 1), dialogs.LastRedactedQuestion);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(vm.Document!.IsDirty);
        Assert.Single(vm.Document.Pages[0].Redactions);

        // Closing with unsaved changes → Save → the warning → Cancel: the document stays open
        dialogs.UnsavedAnswer = UnsavedChoice.Save;
        Assert.False(await vm.ConfirmDiscardAsync());
        Assert.Equal(2, dialogs.RedactedAsked);
        await vm.Document.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task SaveACopyKeepsTheOriginalAndTheWindowShowsTheCopy()
    {
        using var dir = new TempDir();
        var (vm, dialogs, settings, path) = await OpenRedactedAsync(dir);
        var before = File.ReadAllBytes(path);
        dialogs.RedactedAnswer = new RedactedSaveAnswer(RedactedSaveChoice.SaveCopy, false);
        dialogs.SavePath = dir.File("copy.pdf");

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Loc.F("RedactedFileName", "a"), dialogs.SuggestedFileName);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(File.Exists(dialogs.SavePath));
        Assert.Equal(dialogs.SavePath, vm.Document!.FilePath);
        Assert.False(vm.Document.IsDirty);
        Assert.True(settings.WarnOverwriteRedacted);

        // No file picked for the copy: nothing is saved
        vm.Document.AddRedactions([(1, new RectPt(10, 10, 30, 20))], RedactionFill.White);
        dialogs.SavePath = null;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.Document.IsDirty);
        await vm.Document.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task OverwriteWithDontAskAgainSwitchesTheWarningOff()
    {
        using var dir = new TempDir();
        var (vm, dialogs, settings, path) = await OpenRedactedAsync(dir);
        dialogs.RedactedAnswer = new RedactedSaveAnswer(RedactedSaveChoice.Overwrite, DontAskAgain: true);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(path, vm.Document!.FilePath);
        Assert.False(vm.Document.IsDirty);
        Assert.False(settings.WarnOverwriteRedacted);

        vm.Document.AddRedactions([(1, new RectPt(10, 10, 30, 20))], RedactionFill.Black);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.RedactedAsked);
        Assert.False(vm.Document.IsDirty);
        await vm.Document.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task NoQuestionWithoutBoxesOrForAnotherFile()
    {
        using var dir = new TempDir();
        var (vm, dialogs, _, path) = await OpenRedactedAsync(dir);

        // Save As to another file: the original is not touched
        dialogs.SavePath = dir.File("other.pdf");
        await vm.SaveAsCommand.ExecuteAsync(null);
        Assert.Equal(0, dialogs.RedactedAsked);
        Assert.False(vm.Document!.IsDirty);

        // Without boxes, saving over the file is nothing special
        vm.Document.Rotate([0], 90);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, dialogs.RedactedAsked);
        Assert.False(vm.Document.IsDirty);

        // Save As onto the own file with boxes asks as well
        vm.Document.AddRedactions([(0, new RectPt(10, 10, 30, 20))], RedactionFill.Black);
        dialogs.SavePath = vm.Document.FilePath;
        await vm.SaveAsCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.RedactedAsked);
        await vm.Document.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task TheDialogAnswersWithItsButtonsAndClosingMeansCancel()
    {
        var owner = new Window { Width = 700, Height = 500 };
        owner.Show();

        var dialog = new RedactedSaveDialog("a.pdf", 3);
        var answer = dialog.ShowDialog<RedactedSaveAnswer?>(owner);
        await Pump();
        Assert.Equal(Loc.F("RedactedSaveMessage", "a.pdf"), dialog.FindControl<TextBlock>("MessageText")!.Text);
        Assert.Equal(Loc.F("RedactedSaveDetails", 3), dialog.FindControl<TextBlock>("DetailsText")!.Text);
        var box = dialog.FindControl<CheckBox>("DontAskBox")!;
        dialog.MouseDown(CenterOf(box, dialog), MouseButton.Left);
        dialog.MouseUp(CenterOf(box, dialog), MouseButton.Left);
        var overwrite = dialog.FindControl<Button>("OverwriteButton")!;
        dialog.MouseDown(CenterOf(overwrite, dialog), MouseButton.Left);
        dialog.MouseUp(CenterOf(overwrite, dialog), MouseButton.Left);
        Assert.Equal(new RedactedSaveAnswer(RedactedSaveChoice.Overwrite, true), await answer);

        // Enter = the safe choice (save a copy)
        var second = new RedactedSaveDialog("a.pdf", 1);
        var copy = second.ShowDialog<RedactedSaveAnswer?>(owner);
        await Pump();
        second.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(new RedactedSaveAnswer(RedactedSaveChoice.SaveCopy, false), await copy);

        var third = new RedactedSaveDialog("a.pdf", 1);
        var closed = third.ShowDialog<RedactedSaveAnswer?>(owner);
        await Pump();
        third.Close(); // [X]
        Assert.Null(await closed); // the window turns this into Cancel (MainWindow.AskOverwriteRedactedAsync)
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TheSettingsSwitchTheWarningOnAndOff()
    {
        var owner = new Window { Width = 600, Height = 800 };
        owner.Show();
        var settings = new AppSettings { WarnOverwriteRedacted = false };
        var changes = 0;
        var window = new SettingsWindow(settings, () => changes++);
        _ = window.ShowDialog(owner);
        await Pump();
        var box = window.FindControl<CheckBox>("WarnRedactedBox")!;
        Assert.False(box.IsChecked);
        window.MouseDown(CenterOf(box, window), MouseButton.Left);
        window.MouseUp(CenterOf(box, window), MouseButton.Left);
        await Pump();
        Assert.True(settings.WarnOverwriteRedacted);
        Assert.Equal(1, changes);
        Assert.True(new AppSettings().WarnOverwriteRedacted); // on by default
        window.Close();
        owner.Close();
    }
}
