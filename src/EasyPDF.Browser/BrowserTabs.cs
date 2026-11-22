using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.App.Services;
using EasyPDF.Remote;

namespace EasyPDF.Browser;

/// <summary>The other tabs of this site (<see cref="TabExchange"/> over the browser's BroadcastChannel).</summary>
internal sealed class BrowserTabs : IBrowserTabs
{
    /// <summary>How long the other tabs get to say what they have open (they answer within milliseconds).</summary>
    private static readonly TimeSpan ListWait = TimeSpan.FromMilliseconds(300);

    /// <summary>How long writing a copy may take (a large document with many changes).</summary>
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(3);

    private readonly TabExchange _exchange;

    private BrowserTabs(RemoteFiles files) => _exchange = new TabExchange(files, Interop.PostTabMessage);

    /// <summary>Null if the browser cannot talk to other tabs.</summary>
    public static BrowserTabs? TryCreate(RemoteFiles files)
    {
        var tabs = new BrowserTabs(files);
        // Messages arrive from JavaScript on the page's only thread, where the documents live too
        return Interop.OpenTabChannel(tabs._exchange.Receive) ? tabs : null;
    }

    public void ShareDocument(Func<OpenDocumentInfo?> current) =>
        _exchange.CurrentDocument = () => current() is { } info ? (info.Name, info.Document) : null;

    public async Task<IReadOnlyList<TabDocumentInfo>> OtherDocumentsAsync() =>
        [.. (await _exchange.OtherDocumentsAsync(ListWait)).Select(d => new TabDocumentInfo(d.TabId, d.Name, d.PageCount, d.IsEdited))];

    public Task<string?> CopyDocumentAsync(TabDocumentInfo document) =>
        _exchange.CopyAsync(new TabDocument(document.TabId, document.Name, document.PageCount, document.IsEdited), CopyTimeout);
}
