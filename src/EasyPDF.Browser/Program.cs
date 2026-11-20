using System;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using EasyPDF.App;
using EasyPDF.App.Services;
using EasyPDF.Core;
using EasyPDF.Core.Pdfium;
using EasyPDF.Remote;

[assembly: SupportedOSPlatform("browser")]

namespace EasyPDF.Browser;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        // main.js passes the page's address: the API is next to it
        var page = new Uri(args.Length > 0 ? args[0] : "http://localhost/");
        var http = new HttpClient { BaseAddress = new Uri(page, "./") };
        var files = new RemoteFiles(new RemoteApi(http));
        var targets = new BrowserSaveTargets();
        PdfBackend.Current = new RemotePdfBackend(files, targets);
        PlatformFeatures.Files = new BrowserFiles(files, targets);
        PlatformFeatures.Current = PlatformFeatures.Web(WebCommands.IsAvailable);
        await Interop.LoadAsync();
        Interop.SetTitle(AppInfo.Name);
        await BuildAvaloniaApp().StartBrowserAppAsync("out");
        Interop.RemoveSplash();
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>();
}
