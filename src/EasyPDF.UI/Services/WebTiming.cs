using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Views;

namespace EasyPDF.App.Services;

/// <summary>
/// Web version: times of the start and of every dialog in the browser's console (lines "[timing] …"), switched on with
/// <c>?timing</c> in the page's address; <c>?benchmark</c> also opens a few dialogs by itself after the start (each twice:
/// the first time pays for code that runs for the first time). <c>tools/web-timing</c> reads the lines with a browser.
/// </summary>
public static class WebTiming
{
    /// <summary>Milliseconds since the page started loading (the browser's <c>performance.now()</c>); set by the browser program.</summary>
    public static Func<double>? Now { get; set; }

    public static bool Enabled { get; set; }

    public static bool Benchmark { get; set; }

    /// <summary>A point of the start, with the time since the page started loading.</summary>
    public static void Mark(string what)
    {
        if (Enabled && Now is { } now)
            Write($"{what} at {now():F0} ms");
    }

    private static void Write(string text) => Console.WriteLine("[timing] " + text);

    private static double Elapsed(double since) => Now is { } now ? now() - since : 0;

    /// <summary>
    /// Around showing a dialog overlay: how long building the view took, and how long until the first frame with it was
    /// drawn (layout, styles, rendering included).
    /// </summary>
    public static async Task<object?> MeasureDialogAsync(Visual host, Func<DialogView> build, Func<DialogView, Task<object?>> show)
    {
        if (!Enabled || Now is not { } now)
            return await show(build());
        var start = now();
        var view = build();
        var built = Elapsed(start);
        var name = view.GetType().Name;
        var count = _shown.TryGetValue(name, out var n) ? n + 1 : 1;
        _shown[name] = count;
        var shown = show(view);
        if (TopLevel.GetTopLevel(host) is { } top)
            top.RequestAnimationFrame(_ => Dispatcher.UIThread.Post(
                () => Write(string.Create(CultureInfo.InvariantCulture,
                    $"dialog {name} #{count}: built {built:F0} ms, drawn {Elapsed(start):F0} ms")), DispatcherPriority.Background));
        return await shown;
    }

    private static readonly Dictionary<string, int> _shown = [];

    /// <summary>The first frame of the page with the program (once the view is in the page).</summary>
    public static void MarkFirstFrame(Control view)
    {
        if (!Enabled)
            return;
        view.AttachedToVisualTree += (_, _) =>
            TopLevel.GetTopLevel(view)?.RequestAnimationFrame(frameTime => Dispatcher.UIThread.Post(() =>
            {
                Mark("first frame");
                if (Benchmark && view is MainView main)
                    _ = RunBenchmarkAsync(main);
            }, DispatcherPriority.Background));
    }

    /// <summary>Opens and closes a few dialogs, each twice, with pauses (the times come from <see cref="MeasureDialogAsync"/>).</summary>
    private static async Task RunBenchmarkAsync(MainView view)
    {
        await Task.Delay(1500);
        var model = view.ViewModel;
        var dialogs = new (string Name, Func<Task> Open)[]
        {
            ("Settings", () => model.ShowSettingsCommand.ExecuteAsync(null)),
            ("About", () => model.ShowAboutCommand.ExecuteAsync(null)),
            ("Merge", () => model.ConcatenateCommand.ExecuteAsync(null)),
            ("QuickGuide", () => model.ShowQuickGuideCommand.ExecuteAsync(null)),
        };
        foreach (var (name, open) in dialogs)
        {
            for (var i = 0; i < 2; i++)
            {
                var running = open();
                await Task.Delay(1200);
                if (!view.CloseTopDialog())
                    Write($"benchmark: {name} did not open a dialog");
                await running;
                await Task.Delay(500);
            }
        }
        Write("benchmark done");
    }
}
