using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using EasyPDF.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace EasyPDF.Server.Tests;

/// <summary>The server in memory (TestServer) with its own storage folder, a fake clock and the given settings.</summary>
public sealed class TestServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string Folder { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));

    private TestServerHost(WebApplication app, string folder)
    {
        _app = app;
        Folder = folder;
    }

    public IServiceProvider Services => _app.Services;

    /// <summary>The in-memory server's handler, for clients of its own (the browser-side client).</summary>
    public HttpMessageHandler Handler => _app.GetTestServer().CreateHandler();

    public static async Task<TestServerHost> StartAsync(params (string Key, string Value)[] settings)
    {
        var folder = Path.Combine(Path.GetTempPath(), "web-tests-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        var config = new Dictionary<string, string?>
        {
            ["Web:Storage:Folder"] = Path.Combine(folder, "files"),
            ["Web:Storage:KeysFolder"] = Path.Combine(folder, "keys"),
            ["Web:WebRoot"] = Path.Combine(folder, "wwwroot"),
            ["Web:Storage:DownloadsFolder"] = Path.Combine(folder, "downloads"),
        };
        foreach (var (key, value) in settings)
            config[key] = value;
        builder.Configuration.AddInMemoryCollection(config);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        builder.Services.AddSingleton<TimeProvider>(time);
        var app = Program.Build(builder);
        await app.StartAsync();
        var host = new TestServerHost(app, folder);
        host.Time.SetUtcNow(time.GetUtcNow());
        host._time = time;
        return host;
    }

    private FakeTimeProvider? _time;

    /// <summary>Moves the server's clock forward.</summary>
    public void Advance(TimeSpan span) => _time!.Advance(span);

    /// <summary>A browser: keeps the session cookie (Secure cookies are not sent by HttpClient over the test's http).</summary>
    public Browser NewBrowser() => new(_app.GetTestClient());

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(Folder, true); } catch (IOException) { }
    }
}

public sealed class Browser(HttpClient client)
{
    public HttpClient Client { get; } = client;

    public string? Cookie { get; private set; }

    public string? LastSetCookie { get; private set; }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool appHeader = true)
    {
        if (Cookie != null)
            request.Headers.Add("Cookie", Cookie);
        if (appHeader)
            request.Headers.Add(ApiHeaders.Request, "1");
        var response = await Client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            LastSetCookie = values.First();
            Cookie = LastSetCookie.Split(';')[0];
        }
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(new HttpRequestMessage(HttpMethod.Get, url));

    public Task<HttpResponseMessage> UploadAsync(byte[] content, string name = "test.pdf", bool appHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        request.Headers.Add(ApiHeaders.FileName, Uri.EscapeDataString(name));
        return SendAsync(request, appHeader);
    }

    public async Task<RemoteFileInfo> UploadDemoAsync()
    {
        var response = await UploadAsync(await File.ReadAllBytesAsync(TestFiles.Demo), "Annual Report 2025.pdf");
        response.EnsureSuccessStatusCode();
        return (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync(response.Content, RemoteJson.Default.RemoteFileInfo))!;
    }
}

public static class TestFiles
{
    public static string Root
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "EasyPDF.slnx")))
                dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    public static string Demo => Path.Combine(Root, "demo-pdfs", "Annual Report 2025.pdf");
}
