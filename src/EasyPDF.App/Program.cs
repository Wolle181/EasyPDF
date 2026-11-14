using System;
using System.IO;
using System.Linq;
using Avalonia;
using EasyPDF.App.Services;

namespace EasyPDF.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var startup = StartupArguments.Parse(args);
        if (startup.Merge && startup.Files.Count > 0)
        {
            // Explorer starts one process per selected file: the first collects all, the others hand over and exit
            var files = MergeInstances.Collect([.. startup.Files.Select(Path.GetFullPath)], MergeInstances.DefaultName, TimeSpan.FromSeconds(1));
            if (files == null)
                return;
            startup = startup with { Files = files };
        }
        startup = startup with { Files = [.. startup.Files.Select(Path.GetFullPath)] };

        // Already running: the files open in a window of that process
        using var instance = InstanceChannel.Start(InstanceChannel.DefaultName, startup, out var handedOver);
        if (handedOver)
            return;
        App.Startup = startup;
        App.Instance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
