using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Services;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>The menu bar can be switched off (Windows/Linux), because the ribbon offers every command of the menu.</summary>
public class MenuBarTests
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

    private static IEnumerable<ICommand> MenuCommands(NativeMenu menu) =>
        menu.Items.OfType<NativeMenuItem>().SelectMany(i => (i.Command != null ? [i.Command] : Enumerable.Empty<ICommand>())
            .Concat(i.Menu != null ? MenuCommands(i.Menu) : []));

    [AvaloniaFact]
    public async Task EveryCommandOfTheMenuIsAlsoInTheRibbon()
    {
        using var dir = new TempDir();
        var recent = dir.Pdf("recent.pdf", 1);
        var settings = new AppSettings();
        settings.AddRecentFile(recent);
        var window = new MainWindow(settings) { Width = 1400, Height = 700 };
        window.Show();
        await Pump();

        var ribbon = window.GetVisualDescendants().OfType<Ribbon>().Single();
        var inRibbon = new HashSet<ICommand>();
        for (var tab = 0; tab < ribbon.Items.Count; tab++)
        {
            ribbon.SelectedIndex = tab; // only the selected tab is in the visual tree
            await Pump(60);
            foreach (var button in window.GetVisualDescendants().OfType<RibbonButton>())
                if (button.Command != null)
                    inRibbon.Add(button.Command);
        }
        // The "Recent" button's list opens recent files and clears the list
        var recentMenu = (MenuFlyout)window.FindControl<RibbonButton>("RecentButton")!.Flyout!;
        foreach (var item in recentMenu.Items.OfType<MenuItem>())
            if (item.Command != null)
                inRibbon.Add(item.Command);

        var missing = MenuCommands(NativeMenu.GetMenu(window)!).Distinct().Where(c => !inRibbon.Contains(c)).ToList();
        Assert.True(missing.Count == 0, "Not in the ribbon: " + string.Join(", ", missing.Select(CommandName)));
        Assert.Contains(window.ViewModel.ClearRecentCommand, inRibbon);
        window.Close();

        string CommandName(ICommand command) =>
            typeof(EasyPDF.App.ViewModels.MainViewModel).GetProperties().FirstOrDefault(p => p.GetValue(window.ViewModel) == command)?.Name ?? "?";
    }

    [AvaloniaFact]
    public async Task TheMenuBarCanBeSwitchedOnAndOffInAllWindowsAndTheShortcutsKeepWorking()
    {
        var settings = new AppSettings();
        var windows = new WindowManager(settings);
        var first = windows.CreateWindow();
        var second = windows.CreateWindow();
        first.Show();
        second.Show();
        await Pump();
        // Off by default on Windows and Linux; macOS keeps its global menu bar
        Assert.Equal(OperatingSystem.IsMacOS(), first.FindControl<NativeMenuBar>("MenuBar")!.IsVisible);

        var shown = first.View.ShowSettingsAsync();
        await Pump();
        var dialog = first.OwnedWindows.OfType<SettingsWindow>().Single();
        var box = dialog.FindControl<CheckBox>("MenuBarBox")!;
        Assert.Equal(!OperatingSystem.IsMacOS(), box.IsVisible);                      // no option on macOS
        if (OperatingSystem.IsMacOS())
            return;
        Assert.False(box.IsChecked);
        dialog.MouseDown(CenterOf(box, dialog), MouseButton.Left);
        dialog.MouseUp(CenterOf(box, dialog), MouseButton.Left);
        Assert.True(settings.ShowMenuBar);
        Assert.True(first.FindControl<NativeMenuBar>("MenuBar")!.IsVisible);
        Assert.True(second.FindControl<NativeMenuBar>("MenuBar")!.IsVisible);
        dialog.MouseDown(CenterOf(box, dialog), MouseButton.Left);
        dialog.MouseUp(CenterOf(box, dialog), MouseButton.Left);
        Assert.False(settings.ShowMenuBar);
        Assert.False(first.FindControl<NativeMenuBar>("MenuBar")!.IsVisible);
        Assert.False(second.FindControl<NativeMenuBar>("MenuBar")!.IsVisible);
        dialog.Close();
        await shown;

        // The shortcuts do not depend on the menu bar (F1: quick guide)
        first.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        await Pump();
        Assert.Single(first.OwnedWindows.OfType<QuickGuideWindow>());

        // A new window follows the setting too
        var third = windows.CreateWindow();
        third.Show();
        Assert.False(third.FindControl<NativeMenuBar>("MenuBar")!.IsVisible);
        foreach (var window in windows.Windows.ToList())
            window.CloseWindow();
    }
}
