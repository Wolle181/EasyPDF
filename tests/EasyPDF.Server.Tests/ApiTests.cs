using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using EasyPDF.Core.Imaging;
using EasyPDF.Core.Pdfium;
using EasyPDF.Remote;
using EasyPDF.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EasyPDF.Server.Tests;

public class ApiTests
{
    [Fact]
    public async Task UploadDescribesTheFileAndStartsASecureSession()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var info = await browser.UploadDemoAsync();

        Assert.Equal(19, info.PageCount);
        Assert.Equal(19, info.PageSizes.Count);
        Assert.Equal("Annual Report 2025.pdf", info.Name);
        Assert.Matches("^[0-9a-f]{32}$", info.Id);
        var cookie = browser.LastSetCookie!.ToLowerInvariant();
        Assert.StartsWith("__host-session=", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    [Fact]
    public async Task RendersAPageAsPngOfTheRequestedSize()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var info = await browser.UploadDemoAsync();

        var response = await browser.GetAsync($"/api/files/{info.Id}/pages/0?w=300&h=424&rot=0");
        response.EnsureSuccessStatusCode();
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var page = PngCodec.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(300, page.Width);
        Assert.Equal(424, page.Height);
    }

    [Fact]
    public async Task AnotherSessionCannotReachTheFile()
    {
        await using var server = await TestServerHost.StartAsync();
        var owner = server.NewBrowser();
        var info = await owner.UploadDemoAsync();
        var stranger = server.NewBrowser();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/files/{info.Id}/pages/0?w=10&h=10&rot=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/files/{info.Id}/content")).StatusCode);
        var delete = await stranger.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/files/{info.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/files/{info.Id}/pages/0?w=10&h=10&rot=0")).StatusCode);
    }

    [Fact]
    public async Task ChangingRequestsNeedTheAppHeader()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var response = await browser.UploadAsync(await File.ReadAllBytesAsync(TestFiles.Demo), appHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RejectsFilesThatAreNoPdf()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var response = await browser.UploadAsync(Encoding.ASCII.GetBytes("hello, this is not a PDF"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(ApiErrors.NotPdf, (await response.Content.ReadFromJsonAsync(RemoteJson.Default.ApiError))!.Code);
    }

    [Fact]
    public async Task RejectsDamagedPdf()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var response = await browser.UploadAsync(Encoding.ASCII.GetBytes("%PDF-1.4\nbroken"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ApiErrors.Damaged, (await response.Content.ReadFromJsonAsync(RemoteJson.Default.ApiError))!.Code);
    }

    [Fact]
    public async Task RejectsUploadsAboveTheLimit()
    {
        await using var server = await TestServerHost.StartAsync(("Web:Storage:MaxUploadMegabytes", "1"));
        var browser = server.NewBrowser();
        var big = new byte[2 * 1024 * 1024];
        Encoding.ASCII.GetBytes("%PDF-1.4\n").CopyTo(big, 0);
        var response = await browser.UploadAsync(big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task LimitsUploadsPerMinute()
    {
        await using var server = await TestServerHost.StartAsync(("Web:RateLimits:UploadsPerMinute", "2"));
        var browser = server.NewBrowser();
        var demo = await File.ReadAllBytesAsync(TestFiles.Demo);
        Assert.Equal(HttpStatusCode.OK, (await browser.UploadAsync(demo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.UploadAsync(demo)).StatusCode);
        var third = await browser.UploadAsync(demo);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(ApiErrors.TooManyRequests, (await third.Content.ReadFromJsonAsync(RemoteJson.Default.ApiError))!.Code);
    }

    [Fact]
    public async Task BuildWritesTheNewPageOrderWithRotation()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var info = await browser.UploadDemoAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/build")
        {
            Content = JsonContent.Create(new BuildRequest(info.Id, "out.pdf", [new RemotePage(info.Id, 2, 90), new RemotePage(info.Id, 0, 0)]),
                RemoteJson.Default.BuildRequest),
        };
        var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var built = (await response.Content.ReadFromJsonAsync(RemoteJson.Default.RemoteFileInfo))!;
        Assert.Equal(2, built.PageCount);
        Assert.Equal("out.pdf", built.Name);
        Assert.True(built.PageSizes[0].Width > built.PageSizes[0].Height);   // turned by 90°
        Assert.True(built.PageSizes[1].Width < built.PageSizes[1].Height);

        var content = await browser.GetAsync($"/api/files/{built.Id}/content");
        content.EnsureSuccessStatusCode();
        Assert.Equal("application/pdf", content.Content.Headers.ContentType?.MediaType);
        var bytes = await content.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public async Task BuildCanScaleDownPicturesAndTellsSizes()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var info = await browser.UploadDemoAsync();
        Assert.Equal(new FileInfo(TestFiles.Demo).Length, info.Size);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/build")
        {
            Content = JsonContent.Create(new BuildRequest(info.Id, "small.pdf", [.. Enumerable.Range(0, 19).Select(i => new RemotePage(info.Id, i, 0))],
                Core.Documents.ImageReduction.Small), RemoteJson.Default.BuildRequest),
        };
        var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var built = (await response.Content.ReadFromJsonAsync(RemoteJson.Default.RemoteFileInfo))!;
        Assert.Equal(19, built.PageCount);
        Assert.True(built.Size > 0);
        Assert.True(built.ReducedImages >= 0);

        var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/build")
        {
            Content = JsonContent.Create(new BuildRequest(info.Id, "x.pdf", [new RemotePage(info.Id, 0, 0)], new Core.Documents.ImageReduction(1, 5)),
                RemoteJson.Default.BuildRequest),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(invalid)).StatusCode);
    }

    [Fact]
    public async Task BuildRejectsPagesOfForeignFiles()
    {
        await using var server = await TestServerHost.StartAsync();
        var owner = server.NewBrowser();
        var info = await owner.UploadDemoAsync();
        var stranger = server.NewBrowser();
        var own = await stranger.UploadDemoAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/build")
        {
            Content = JsonContent.Create(new BuildRequest(own.Id, "x.pdf", [new RemotePage(info.Id, 0, 0)]), RemoteJson.Default.BuildRequest),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await stranger.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task SplitReturnsTheZipWithAllParts()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var info = await browser.UploadDemoAsync();
        RemotePage[] pages = [.. Enumerable.Range(0, 19).Select(i => new RemotePage(info.Id, i, 0))];
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/split")
        {
            Content = JsonContent.Create(new SplitDocumentRequest(pages, Core.Documents.SplitPlan.EveryN(19, 5), "../Report"),
                RemoteJson.Default.SplitDocumentRequest),
        };
        var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal(["Report_part01.pdf", "Report_part02.pdf", "Report_part03.pdf", "Report_part04.pdf"], zip.Entries.Select(e => e.Name).Order());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(server.Folder, "files"), "split-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SplitRejectsPartsOutsideThePages()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var info = await browser.UploadDemoAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/split")
        {
            Content = JsonContent.Create(new SplitDocumentRequest([new RemotePage(info.Id, 0, 0)], [new Core.Documents.SplitPart(0, 5)], "x"),
                RemoteJson.Default.SplitDocumentRequest),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task ListsAndReadsAttachments()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var path = Path.Combine(server.Folder, "with-attachments.pdf");
        Directory.CreateDirectory(server.Folder);
        var invoice = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><rsm:CrossIndustryInvoice/>");
        Core.Tests.TestPdf.WithAttachments(path, ("factur-x.xml", invoice), ("notes.txt", Encoding.UTF8.GetBytes("notes")));
        var upload = await browser.UploadAsync(await File.ReadAllBytesAsync(path));
        upload.EnsureSuccessStatusCode();
        var info = (await upload.Content.ReadFromJsonAsync(RemoteJson.Default.RemoteFileInfo))!;

        var list = (await (await browser.GetAsync($"/api/files/{info.Id}/attachments")).Content
            .ReadFromJsonAsync(RemoteJson.Default.IReadOnlyListPdfAttachment))!;
        Assert.Equal(2, list.Count);
        Assert.Contains(list, a => a.IsEInvoice);
        var first = list.Single(a => a.Name == "factur-x.xml");
        Assert.Equal(invoice, await (await browser.GetAsync($"/api/files/{info.Id}/attachments/{first.Index}")).Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/api/files/{info.Id}/attachments/9")).StatusCode);
    }

    [Fact]
    public async Task BlankPageHasTheRequestedSize()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/blank")
        {
            Content = JsonContent.Create(new BlankRequest(300, 500), RemoteJson.Default.BlankRequest),
        };
        var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var info = (await response.Content.ReadFromJsonAsync(RemoteJson.Default.RemoteFileInfo))!;
        Assert.Equal(1, info.PageCount);
        Assert.Equal(300, info.PageSizes[0].Width, 0.5);
        Assert.Equal(500, info.PageSizes[0].Height, 0.5);
    }

    [Fact]
    public async Task DeletedFilesAreGone()
    {
        await using var server = await TestServerHost.StartAsync();
        var browser = server.NewBrowser();
        var info = await browser.UploadDemoAsync();
        var delete = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/files/{info.Id}"));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/api/files/{info.Id}/content")).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(server.Folder, "files"), "*.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FilesExpireAfterTheIdleTime()
    {
        await using var server = await TestServerHost.StartAsync(("Web:Storage:IdleTimeoutMinutes", "30"),
            ("Web:Authentication:SessionIdleMinutes", "600"));
        var browser = server.NewBrowser();
        var info = await browser.UploadDemoAsync();
        var cleanup = server.Services.GetRequiredService<CleanupService>();

        server.Advance(TimeSpan.FromMinutes(20));
        await cleanup.RunOnceAsync();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/api/files/{info.Id}/content")).StatusCode); // used: idle time starts again

        server.Advance(TimeSpan.FromMinutes(31));
        await cleanup.RunOnceAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/api/files/{info.Id}/content")).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(server.Folder, "files"), "*.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FilesExpireAfterTheMaximumAgeEvenWhenUsed()
    {
        await using var server = await TestServerHost.StartAsync(("Web:Storage:MaxAgeHours", "1"),
            ("Web:Authentication:SessionIdleMinutes", "600"));
        var browser = server.NewBrowser();
        var info = await browser.UploadDemoAsync();
        var cleanup = server.Services.GetRequiredService<CleanupService>();
        for (var i = 0; i < 5; i++)
        {
            server.Advance(TimeSpan.FromMinutes(15));
            await browser.GetAsync($"/api/files/{info.Id}/content");
            await cleanup.RunOnceAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/api/files/{info.Id}/content")).StatusCode);
    }

    [Fact]
    public async Task AnExpiredSessionLosesItsFilesAndGetsANewCookie()
    {
        await using var server = await TestServerHost.StartAsync(("Web:Authentication:SessionIdleMinutes", "10"));
        var browser = server.NewBrowser();
        var info = await browser.UploadDemoAsync();
        var oldCookie = browser.Cookie;

        server.Advance(TimeSpan.FromMinutes(11));
        await server.Services.GetRequiredService<CleanupService>().RunOnceAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/api/files/{info.Id}/content")).StatusCode);
        Assert.NotEqual(oldCookie, browser.Cookie);
    }

    [Fact]
    public async Task EveryResponseCarriesTheSecurityHeaders()
    {
        await using var server = await TestServerHost.StartAsync();
        var response = await server.NewBrowser().GetAsync("/health");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("'wasm-unsafe-eval'", csp);
        Assert.DoesNotContain("'unsafe-eval'", csp.Replace("'wasm-unsafe-eval'", ""));
        Assert.Contains("object-src 'none'", csp);
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task SingleSignOnModeNeedsASignedInUser()
    {
        await using var server = await TestServerHost.StartAsync(("Web:Authentication:Mode", "Oidc"),
            ("Web:Authentication:Authority", "https://sso.invalid/realms/test"), ("Web:Authentication:ClientId", "app"));
        var browser = server.NewBrowser();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.UploadAsync(await File.ReadAllBytesAsync(TestFiles.Demo))).StatusCode);
    }

    [Fact]
    public async Task ConfigTellsTheModeAndLimits()
    {
        await using var server = await TestServerHost.StartAsync(("Web:Storage:MaxUploadMegabytes", "50"));
        var config = await (await server.NewBrowser().GetAsync("/api/config")).Content.ReadFromJsonAsync(RemoteJson.Default.ServerConfig);
        Assert.Equal("None", config!.AuthenticationMode);
        Assert.Equal(50L * 1024 * 1024, config.MaxUploadBytes);
    }

    [Fact]
    public async Task ServesTheBrowserProgramWithFallbackToIndex()
    {
        await using var server = await TestServerHost.StartAsync();
        var root = Path.Combine(server.Folder, "wwwroot");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<html>app</html>");
        // the web root is looked up at start-up: a server started with an existing folder serves it
        await using var second = await TestServerHostWithRoot(root);
        var browser = second.NewBrowser();
        Assert.Contains("app", await (await browser.GetAsync("/")).Content.ReadAsStringAsync());
        Assert.Contains("app", await (await browser.GetAsync("/some/deep/link")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LibraryHasTheSampleStampsAndTheSharedStampsAndTemplates()
    {
        await using var probe = await TestServerHost.StartAsync();
        var shared = Path.Combine(probe.Folder, "shared");
        Directory.CreateDirectory(Path.Combine(shared, "stamps", "Company"));
        Directory.CreateDirectory(Path.Combine(shared, "templates"));
        await File.WriteAllBytesAsync(Path.Combine(shared, "stamps", "Checked.png"), [0x89, 0x50]);
        await File.WriteAllBytesAsync(Path.Combine(shared, "stamps", "Company", "Logo.png"), [0x89, 0x50]);
        await File.WriteAllTextAsync(Path.Combine(shared, "stamps", "readme.txt"), "not a stamp");
        await File.WriteAllTextAsync(Path.Combine(shared, "templates", "Greeting.txt"), "Kind regards");
        await File.WriteAllBytesAsync(Path.Combine(shared, "templates", "Huge.txt"), new byte[Library.MaxFileBytes + 1]);
        await using var server = await TestServerHost.StartAsync(("Web:Storage:SharedFolder", shared));

        var response = await server.NewBrowser().GetAsync("/api/library");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("samples/Numbers/01.png", names);
        Assert.Contains("shared/stamps/Checked.png", names);
        Assert.Contains("shared/stamps/Company/Logo.png", names);
        Assert.Contains("shared/templates/Greeting.txt", names);
        Assert.DoesNotContain("shared/stamps/readme.txt", names);
        Assert.DoesNotContain("shared/templates/Huge.txt", names);
    }

    private static Task<TestServerHost> TestServerHostWithRoot(string root) => TestServerHost.StartAsync(("Web:WebRoot", root));
}

public class CodecAndSetupTests
{
    [Fact]
    public void PngRoundTripKeepsThePixels()
    {
        var pixels = new byte[3 * 4 * 2];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i * 7);
            pixels[i + 1] = (byte)(i * 13);
            pixels[i + 2] = (byte)(255 - i);
            pixels[i + 3] = 255;
        }
        var page = new RenderedPage(3, 2, 12, pixels);
        var back = PngCodec.Decode(PngCodec.Encode(page));
        Assert.Equal(3, back.Width);
        Assert.Equal(2, back.Height);
        Assert.Equal(pixels, back.Pixels);
    }

    [Theory]
    [InlineData("Iface\tDestination\tGateway \tFlags\nEth0\t00000000\t010011AC\t0003\n", "172.17.0.1")]
    [InlineData("Iface\tDestination\tGateway \tFlags\nEth0\t0011AC00\t00000000\t0001\n", null)]
    public void ReadsTheContainerGateway(string table, string? expected)
    {
        var found = Security.ForwardedHeadersSetup.TryParseDefaultGateway(table, out var gateway);
        Assert.Equal(expected != null, found);
        if (expected != null)
            Assert.Equal(expected, gateway.ToString());
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd.pdf")]
    [InlineData("Report.PDF", "Report.PDF")]
    [InlineData("", "document.pdf")]
    [InlineData("a\"b.pdf", "ab.pdf")]
    public void CleansFileNames(string input, string expected) => Assert.Equal(expected, FileStore.CleanName(input));
}
