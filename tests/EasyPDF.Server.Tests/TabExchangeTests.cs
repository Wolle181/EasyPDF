using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Remote;
using Xunit;

namespace EasyPDF.Server.Tests;

/// <summary>
/// Two browser tabs of the same browser (one session cookie) hand documents over: the protocol of
/// <see cref="TabExchange"/> with the server in memory and a channel in place of the browser's BroadcastChannel.
/// </summary>
public class TabExchangeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(300);

    /// <summary>The browser's cookie jar, shared by all tabs.</summary>
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

    /// <summary>Like a BroadcastChannel: a message reaches every other tab, later and not the sender.</summary>
    private sealed class Channel
    {
        private readonly List<TabExchange> _tabs = [];

        public TabExchange Join(RemoteFiles files)
        {
            TabExchange? tab = null;
            tab = new TabExchange(files, message =>
            {
                foreach (var other in _tabs.Where(t => t != tab).ToList())
                    _ = Task.Run(() => other.Receive(message));
            });
            _tabs.Add(tab);
            return tab;
        }

        public void Leave(TabExchange tab) => _tabs.Remove(tab);
    }

    private sealed record Tab(TabExchange Exchange, RemotePdfBackend Backend);

    private static Func<Tab> Browser(TestServerHost server, Channel channel)
    {
        var http = new HttpClient(new CookieHandler(server.Handler)) { BaseAddress = new Uri("http://localhost/") };
        return () =>
        {
            var files = new RemoteFiles(new RemoteApi(http));
            return new Tab(channel.Join(files), new RemotePdfBackend(files, null!));
        };
    }

    private static async Task<EditableDocument> OpenDemoAsync(Tab tab)
    {
        await using var file = File.OpenRead(TestFiles.Demo);
        var info = await tab.Backend.Files.Api.UploadAsync(file, "Annual Report 2025.pdf", file.Length);
        var document = EditableDocument.FromSource(await tab.Backend.OpenAsync(tab.Backend.Files.Add(info)));
        tab.Exchange.CurrentDocument = () => ("Annual Report 2025.pdf", document);
        return document;
    }

    [Fact]
    public async Task ATabCopiesTheDocumentOfAnotherWithItsUnsavedChanges()
    {
        await using var server = await TestServerHost.StartAsync();
        var newTab = Browser(server, new Channel());
        var asking = newTab();
        var other = newTab();
        var empty = newTab();
        empty.Exchange.CurrentDocument = () => null;               // a tab without a document does not answer
        await using var document = await OpenDemoAsync(other);
        document.Rotate([0], 90);
        document.Delete([18]);

        var found = Assert.Single(await asking.Exchange.OtherDocumentsAsync(Wait));
        Assert.Equal(new TabDocument(other.Exchange.TabId, "Annual Report 2025.pdf", 18, true), found);

        var path = await asking.Exchange.CopyAsync(found, TimeSpan.FromSeconds(30));
        Assert.NotNull(path);
        await using var copy = await asking.Backend.OpenAsync(path);
        Assert.Equal(18, copy.PageCount);
        Assert.True(copy.TryGetPageSize(0, out var size));
        Assert.True(size.Width > size.Height);                      // the turned title page, written into the copy
        Assert.Equal(19, document.Sources[0].PageCount);             // the other tab keeps its document as it was
        Assert.True(document.IsDirty);
    }

    [Fact]
    public async Task ACopyOfAClosedTabFailsAndDoesNotWait()
    {
        await using var server = await TestServerHost.StartAsync();
        var channel = new Channel();
        var newTab = Browser(server, channel);
        var asking = newTab();
        var other = newTab();
        await using var document = await OpenDemoAsync(other);
        var found = Assert.Single(await asking.Exchange.OtherDocumentsAsync(Wait));

        channel.Leave(other.Exchange);                               // the tab was closed meanwhile
        Assert.Empty(await asking.Exchange.OtherDocumentsAsync(Wait));
        Assert.Null(await asking.Exchange.CopyAsync(found, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task MessagesForOtherTabsAndGarbageAreIgnored()
    {
        await using var server = await TestServerHost.StartAsync();
        var newTab = Browser(server, new Channel());
        var tab = newTab();
        var sent = new List<string>();
        var listening = new TabExchange(tab.Backend.Files, sent.Add);
        await using var document = await OpenDemoAsync(tab);
        listening.CurrentDocument = tab.Exchange.CurrentDocument;

        listening.Receive("not json");
        listening.Receive("""{"kind":"list","from":"x","to":"somebody else","request":1}""");
        Assert.Empty(sent);
        listening.Receive("""{"kind":"list","from":"x","to":null,"request":7}""");
        var answer = Assert.Single(sent);
        Assert.Contains("\"kind\":\"document\"", answer);
        Assert.Contains("\"to\":\"x\"", answer);
        Assert.Contains("\"request\":7", answer);
    }
}
