using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using EasyPDF.App;
using EasyPDF.App.Services;
using EasyPDF.Core;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using EasyPDF.Remote;

[assembly: SupportedOSPlatform("browser")]

namespace EasyPDF.Browser;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        // main.js passes the page's address: the API is next to it
        var page = new Uri(args.Length > 0 ? args[0] : "http://localhost/");
        // ?timing: times of the start and the dialogs in the console; ?benchmark also opens dialogs by itself
        WebTiming.Now = Interop.Now;
        WebTiming.Benchmark = HasQueryFlag(page, "benchmark");
        WebTiming.Enabled = WebTiming.Benchmark || HasQueryFlag(page, "timing");
        WebTiming.Mark("program started");
        var http = new HttpClient { BaseAddress = new Uri(page, "./") };
        var api = new RemoteApi(http);
        var files = new RemoteFiles(api);
        var targets = new BrowserSaveTargets();
        PdfBackend.Current = new RemotePdfBackend(files, targets);
        PlatformFeatures.Files = new BrowserFiles(files, targets);
        PlatformFeatures.OcrEngineFactory = () => new ServerOcrOnly();
        TextMetrics.EmbeddedFonts = "avares://EasyPDF.Browser/Assets/Fonts";
        PlatformFeatures.Current = WebCommands.Features;
        await Interop.LoadAsync();
        WebTiming.Mark("interop loaded");
        PlatformFeatures.Tabs = BrowserTabs.TryCreate(files);
        Interop.SetTitle(AppInfo.Name);
        // Settings (theme, language) from the browser's storage into the file the program reads, and back after each change
        var stored = Interop.LoadSettings();
        if (stored.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppSettings.DefaultPath)!);
            File.WriteAllText(AppSettings.DefaultPath, stored);
        }
        AppSettings.Saved += Interop.SaveSettings;
        WebTiming.Mark("settings");
        // What the program asks the server before it shows itself, side by side instead of one after the other (each is a
        // round trip). The configuration comes first: on the first visit its answer sets the session cookie, which
        // parallel API requests would otherwise each get anew (sessions without use). The videos are no API requests.
        var videos = LoadVideosAsync(http);
        await LoadServerInfoAsync(api);
        await Task.WhenAll(LoadDownloadsAsync(api), LoadLibraryAsync(http), videos);
        WebTiming.Mark("server requests");
        await BuildAvaloniaApp().StartBrowserAppAsync("out");
        WebTiming.Mark("Avalonia started");
        Interop.RemoveSplash();
    }

    /// <summary>When and how the server is deployed (Help > About).</summary>
    private static async Task LoadServerInfoAsync(RemoteApi api)
    {
        try
        {
            var config = await api.GetConfigAsync();
            VersionInfo.WebServer = new WebServerInfo(config.DeployedAt, config.InContainer);
        }
        catch (Exception ex) when (ex is HttpRequestException or RemoteApiException or System.Text.Json.JsonException)
        {
        }
    }

    /// <summary>Desktop packages the server offers for download (none: the button stays hidden).</summary>
    private static async Task LoadDownloadsAsync(RemoteApi api)
    {
        try
        {
            var packages = await api.ListDownloadsAsync();
            if (packages.Count > 0)
                DesktopDownloads.Web = new DesktopDownloads.WebPackages(packages, DesktopDownloads.KindsFor(Interop.Platform()),
                    package => Interop.Download(RemoteApi.DownloadUrl(package)));
        }
        catch (Exception ex) when (ex is HttpRequestException or RemoteApiException or System.Text.Json.JsonException)
        {
        }
    }

    /// <summary>Stamps and templates: the user's own from the browser's storage, samples and shared ones from the server.</summary>
    private static async Task LoadLibraryAsync(HttpClient http) => PlatformFeatures.Library = await BrowserLibrary.LoadAsync(http);

    /// <summary>The brand's introduction videos that are next to the page (none: the video button stays hidden).</summary>
    private static async Task LoadVideosAsync(HttpClient http)
    {
        var found = await Task.WhenAll(new[] { "de", "en" }.Select(async language =>
        {
            try
            {
                using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"videos/introduction.{language}.mp4"));
                return head.IsSuccessStatusCode ? language : null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }));
        IntroVideo.Web = new IntroVideo.WebVideos([.. found.OfType<string>()], language =>
        {
            Interop.PlayVideo($"videos/introduction.{language}.mp4", EasyPDF.App.Localization.Loc.T("Close"));
            return Task.CompletedTask;
        });
    }

    private static bool HasQueryFlag(Uri page, string name) =>
        page.Query.TrimStart('?').Split('&').Any(p => p.Split('=')[0].Equals(name, StringComparison.OrdinalIgnoreCase));

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>();
}
