using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using EasyPDF.Remote;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace EasyPDF.Server.Tests;

/// <summary>The browser-side client (RemoteApi, RemotePdfSource, RemotePdfBackend) against the server in memory.</summary>
public class RemoteClientTests
{
    /// <summary>Keeps the session cookie like a browser (HttpClient does not send Secure cookies over the test's http).</summary>
    private sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private string? _cookie;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (_cookie != null)
                request.Headers.Add("Cookie", _cookie);
            var response = await base.SendAsync(request, token);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                _cookie = values.First().Split(';')[0];
            return response;
        }
    }

    private sealed class MemoryTargets : ISaveTargets
    {
        public Dictionary<string, byte[]> Written { get; } = [];

        public bool Has(string path) => path.StartsWith("/save/", StringComparison.Ordinal);

        public Task WriteAsync(string path, byte[] content, CancellationToken token)
        {
            Written[path] = content;
            return Task.CompletedTask;
        }
    }

    private static (RemotePdfBackend Backend, MemoryTargets Targets) Client(TestServerHost server)
    {
        var handler = new CookieHandler(server.Handler);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var targets = new MemoryTargets();
        return (new RemotePdfBackend(new RemoteFiles(new RemoteApi(http)), targets), targets);
    }

    private static async Task<string> UploadDemoAsync(RemotePdfBackend backend)
    {
        await using var file = File.OpenRead(TestFiles.Demo);
        var info = await backend.Files.Api.UploadAsync(file, "Annual Report 2025.pdf", file.Length);
        return backend.Files.Add(info);
    }

    [Fact]
    public async Task OpensAndRendersAnUploadedFile()
    {
        await using var server = await TestServerHost.StartAsync();
        var (backend, _) = Client(server);
        var path = await UploadDemoAsync(backend);

        Assert.StartsWith(RemoteFiles.Prefix, path);
        Assert.True(backend.Exists(path));
        Assert.False(backend.CanOverwrite(path));      // the browser cannot write back to an upload
        await using var source = await backend.OpenAsync(path);
        Assert.Equal(19, source.PageCount);
        Assert.Equal("Annual Report 2025.pdf", source.FileName);
        Assert.True(source.TryGetPageSize(18, out var size));
        Assert.True(size.Width > 0);
        var page = await source.RenderAsync(0, 120, 170);
        Assert.Equal(120, page.Width);
        Assert.Equal(170, page.Height);
        var words = await source.ReadWordsAsync(1);
        Assert.Contains(words, w => w.Text.Equals("CONTENTS", StringComparison.OrdinalIgnoreCase));
        Assert.True(await source.CountTextCharsAsync(1) > 0);
    }

    [Fact]
    public async Task TheMainFontOfAPageComesFromTheServerAndAPageWithoutTextHasNone()
    {
        await using var server = await TestServerHost.StartAsync();
        var (backend, _) = Client(server);
        await using var source = await backend.OpenAsync(await UploadDemoAsync(backend));
        var font = await source.ReadMainFontAsync(2);
        Assert.NotNull(font);
        Assert.InRange(font!.Size, 6, 20);
        Assert.False(string.IsNullOrEmpty(font.BaseFont));
        await using var blank = await backend.CreateBlankAsync(new PageSizePt(200, 100));
        Assert.Null(await blank.ReadMainFontAsync(0));                // answer 204
    }

    [Fact]
    public async Task SavingAnEditedDocumentWritesTheTargetAndReopensTheResult()
    {
        await using var server = await TestServerHost.StartAsync();
        var (backend, targets) = Client(server);
        var path = await UploadDemoAsync(backend);
        var source = await backend.OpenAsync(path);
        await using var document = EditableDocument.FromSource(source);
        document.Rotate([0], 90);
        document.Delete([1, 2]);

        var previous = PdfBackend.Current;
        PdfBackend.Current = backend;
        try
        {
            await document.SaveAsync("/save/abc/Result.pdf");
        }
        finally
        {
            PdfBackend.Current = previous;
        }
        var bytes = targets.Written["/save/abc/Result.pdf"];
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.Equal(17, document.PageCount);
        Assert.False(document.IsDirty);
        Assert.StartsWith(RemoteFiles.Prefix, document.FilePath);
        Assert.EndsWith("/Result.pdf", document.FilePath);
        Assert.True(document.Pages[0].DisplaySize.Width > document.Pages[0].DisplaySize.Height); // rotation written
    }

    [Fact]
    public async Task SavingAsImageOnTheServerLeavesNoTextAndRefusesOtherResolutions()
    {
        await using var server = await TestServerHost.StartAsync();
        var (backend, targets) = Client(server);
        var path = await UploadDemoAsync(backend);
        var source = await backend.OpenAsync(path);
        await using var document = EditableDocument.FromSource(source);
        document.Delete(Enumerable.Range(2, document.PageCount - 2));
        Assert.NotEmpty(await source.ReadWordsAsync(1));

        var previous = PdfBackend.Current;
        PdfBackend.Current = backend;
        try
        {
            await document.SaveAsync("/save/img/Fixed.pdf", rasterize: new EasyPDF.Core.Documents.PageRasterization(150, true));
        }
        finally
        {
            PdfBackend.Current = previous;
        }
        Assert.True(targets.Written.ContainsKey("/save/img/Fixed.pdf"));
        Assert.Equal(2, document.PageCount);
        Assert.Empty(await document.BaseSource.ReadWordsAsync(1));        // the reopened result: pictures only

        var request = new BuildRequest(RemotePdfBackend.IdOf(document.BaseSource), "x.pdf", RemotePdfBackend.ToRemote(document.Pages),
            Rasterize: new EasyPDF.Core.Documents.PageRasterization(1200, false));
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => backend.Files.Api.BuildAsync(request, CancellationToken.None));
        Assert.Equal(ApiErrors.Invalid, error.Code);
    }

    [Fact]
    public async Task InsertsABlankPageMadeByTheServer()
    {
        await using var server = await TestServerHost.StartAsync();
        var (backend, _) = Client(server);
        var path = await UploadDemoAsync(backend);
        var previous = PdfBackend.Current;
        PdfBackend.Current = backend;
        try
        {
            await using var document = EditableDocument.FromSource(await backend.OpenAsync(path));
            await document.InsertBlankAsync(0, new PageSizePt(200, 300));
            Assert.Equal(20, document.PageCount);
            Assert.Equal(200, document.Pages[0].BaseSize.Width, 0.5);
        }
        finally
        {
            PdfBackend.Current = previous;
        }
    }

    [Fact]
    public async Task TheLastClosedSourceDeletesTheFileOnTheServer()
    {
        await using var server = await TestServerHost.StartAsync();
        var (backend, _) = Client(server);
        var path = await UploadDemoAsync(backend);
        var first = await backend.OpenAsync(path);
        var second = await backend.OpenAsync(path);
        var id = ((RemotePdfSource)first).Id;

        first.Dispose();
        await Task.Delay(300);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(server.Folder, "files"), id + ".pdf", SearchOption.AllDirectories));
        second.Dispose();
        for (var i = 0; i < 50 && Directory.EnumerateFiles(Path.Combine(server.Folder, "files"), id + ".pdf", SearchOption.AllDirectories).Any(); i++)
            await Task.Delay(100);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(server.Folder, "files"), id + ".pdf", SearchOption.AllDirectories));
        Assert.False(backend.Exists(path));
    }

    [Fact]
    public async Task SplittingWritesOneZipToTheTarget()
    {
        await using var server = await TestServerHost.StartAsync();
        var (backend, targets) = Client(server);
        var path = await UploadDemoAsync(backend);
        await using var document = EditableDocument.FromSource(await backend.OpenAsync(path));
        var count = await backend.SplitAsync(document.Pages, Core.Documents.SplitPlan.EveryN(19, 10), "/save/z/parts.zip", "Report");
        Assert.Equal(2, count);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(targets.Written["/save/z/parts.zip"]));
        Assert.Equal(2, zip.Entries.Count);
    }

    [Fact]
    public async Task TextsRedactionsAndStampsArriveInTheSavedFile()
    {
        await using var server = await TestServerHost.StartAsync();
        var (backend, targets) = Client(server);
        var path = await UploadDemoAsync(backend);
        var source = await backend.OpenAsync(path);
        await using var document = EditableDocument.FromSource(source);
        // Page 2 is the table of contents: black out its heading, write a text, place a small red stamp
        var heading = (await source.ReadWordsAsync(1)).First(w => w.Text.Equals("CONTENTS", StringComparison.OrdinalIgnoreCase));
        document.AddRedactions([(1, heading.Box)], RedactionFill.Black);
        document.AddText(1, "Webtext", "Helvetica", 14, "#000000", new RectPt(60, 700, 200, 20));
        var bgra = Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 20 * 10).SelectMany(b => b).ToArray();
        document.AddStamp(1, [], 20, 10, new RectPt(300, 400, 40, 20), 0, bgra);

        var previous = PdfBackend.Current;
        PdfBackend.Current = backend;
        try
        {
            await document.SaveAsync("/save/o/overlays.pdf");
        }
        finally
        {
            PdfBackend.Current = previous;
        }
        var file = Path.Combine(server.Folder, "overlays-result.pdf");
        await File.WriteAllBytesAsync(file, targets.Written["/save/o/overlays.pdf"]);
        await using var saved = await PdfSource.OpenAsync(file);
        var words = (await saved.ReadWordsAsync(1)).Select(w => w.Text).ToList();
        Assert.Contains("Webtext", words);
        Assert.DoesNotContain(words, w => w.Equals("CONTENTS", StringComparison.OrdinalIgnoreCase)); // removed for good
        var page = await saved.RenderAsync(1, 595, 842);
        var red = 0;
        for (var y = 400; y < 420; y++)
            for (var x = 300; x < 340; x++)
            {
                var i = y * page.Stride + x * 4;
                if (page.Pixels[i + 2] > 200 && page.Pixels[i + 1] < 60 && page.Pixels[i] < 60)
                    red++;
            }
        Assert.True(red > 400, $"stamp not found ({red} red pixels)");
    }

    [Fact]
    public async Task TextRecognitionRunsOnTheServer()
    {
        await using var server = await TestServerHost.StartAsync();
        var (backend, _) = Client(server);
        var path = await UploadDemoAsync(backend);
        await using var document = EditableDocument.FromSource(await backend.OpenAsync(path));
        // PageOcr asks the server (the engine passed here must never be used)
        var found = await Core.Ocr.PageOcr.RecognizePagesAsync(new ServerOcrOnly(), [document.Pages[1]], skipPagesWithText: false);
        var words = found[document.Pages[1].Id];
        Assert.Contains(words, w => w.Text.Contains("CONTENTS", StringComparison.OrdinalIgnoreCase));
        Assert.All(words, w => Assert.True(w.Box.X >= 0 && w.Box.Right <= 600));   // page space (points), not pixels
    }

    [Fact]
    public async Task ServerErrorsArriveWithTheirCode()
    {
        await using var server = await TestServerHost.StartAsync();
        var (backend, _) = Client(server);
        using var notPdf = new MemoryStream(Encoding.ASCII.GetBytes("plain text"));
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => backend.Files.Api.UploadAsync(notPdf, "x.pdf", notPdf.Length));
        Assert.Equal(ApiErrors.NotPdf, error.Code);
    }

    /// <summary>Stands in for the network between browser and server: fails the connection or answers with a web page.</summary>
    private sealed class NetworkHandler(Func<HttpResponseMessage?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            answer() is { } response ? Task.FromResult(response) : throw new HttpRequestException("Load failed");
    }

    [Theory]
    [InlineData(0)]     // connection failed or cut off (Safari: "Load failed")
    [InlineData(403)]   // a company's web filter answers with its own page
    [InlineData(200)]   // a proxy's login page
    public async Task AnUploadThatDoesNotReachTheServerFailsWithNoConnection(int status)
    {
        var handler = new NetworkHandler(() => status == 0 ? null : new HttpResponseMessage((System.Net.HttpStatusCode)status)
        {
            Content = new StringContent("<html><body>Blocked</body></html>", Encoding.UTF8, "text/html"),
        });
        var api = new RemoteApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        await using var file = File.OpenRead(TestFiles.Demo);
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => api.UploadAsync(file, "a.pdf", file.Length));
        Assert.Equal(ApiErrors.NoConnection, error.Code);
    }
}
