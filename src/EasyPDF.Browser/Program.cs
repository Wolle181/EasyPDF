using System;
using System.Collections.Generic;
using System.IO;
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
        var http = new HttpClient { BaseAddress = new Uri(page, "./") };
        var api = new RemoteApi(http);
        var files = new RemoteFiles(api);
        var targets = new BrowserSaveTargets();
        PdfBackend.Current = new RemotePdfBackend(files, targets);
        PlatformFeatures.Files = new BrowserFiles(files, targets);
        PlatformFeatures.OcrEngineFactory = () => new ServerOcrOnly();
        TextMetrics.EmbeddedFonts = "avares://EasyPDF.Browser/Assets/Fonts";
        PlatformFeatures.Current = PlatformFeatures.Web(WebCommands.IsAvailable, WebCommands.Buttons);
        await Interop.LoadAsync();
        Interop.SetTitle(AppInfo.Name);
        // Settings (theme, language) from the browser's storage into the file the program reads, and back after each change
        var stored = Interop.LoadSettings();
        if (stored.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppSettings.DefaultPath)!);
            File.WriteAllText(AppSettings.DefaultPath, stored);
        }
        AppSettings.Saved += Interop.SaveSettings;
        // When and how the server is deployed (Help > About)
        try
        {
            var config = await api.GetConfigAsync();
            VersionInfo.WebServer = new WebServerInfo(config.DeployedAt, config.InContainer);
        }
        catch (Exception ex) when (ex is HttpRequestException or RemoteApiException or System.Text.Json.JsonException)
        {
        }
        // Desktop packages the server offers for download (none: the button stays hidden)
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
        // Stamps and templates: the user's own from the browser's storage, samples and shared ones from the server
        PlatformFeatures.Library = await BrowserLibrary.LoadAsync(http);
        // The brand's introduction videos that are next to the page (none: the video button stays hidden)
        var languages = new List<string>();
        foreach (var language in new[] { "de", "en" })
        {
            try
            {
                using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"videos/introduction.{language}.mp4"));
                if (head.IsSuccessStatusCode)
                    languages.Add(language);
            }
            catch (HttpRequestException)
            {
            }
        }
        IntroVideo.Web = new IntroVideo.WebVideos(languages, language =>
        {
            Interop.PlayVideo($"videos/introduction.{language}.mp4", EasyPDF.App.Localization.Loc.T("Close"));
            return Task.CompletedTask;
        });
        await BuildAvaloniaApp().StartBrowserAppAsync("out");
        Interop.RemoveSplash();
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>();
}
