using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;

namespace EasyPDF.Remote;

/// <summary>The document another browser tab of the same site has open, as that tab described it.</summary>
public sealed record TabDocument(string TabId, string Name, int PageCount, bool IsEdited);

/// <summary>One message between the tabs (JSON over the browser's BroadcastChannel).</summary>
/// <param name="Kind">"list" (who has a document?), "document" (answer), "copy" (write yours on the server), "copied" (answer).</param>
/// <param name="To">The addressed tab; null for all.</param>
/// <param name="File">"copied": the file the server wrote; null if it could not be written.</param>
public sealed record TabMessage(string Kind, string From, string? To, int Request, string? Name = null, int PageCount = 0,
    bool IsEdited = false, RemoteFileInfo? File = null);

/// <summary>
/// The documents of the other tabs of this site in the same browser. All tabs share the session cookie, so a file one tab
/// has the server write can be opened by another. Asked for a copy, a tab has the server write its document as it is now
/// (with its unsaved changes, like saving) and hands the new file over; the asking tab then owns it (it is deleted on the
/// server when its last source is disposed). The transport (BroadcastChannel) is outside: <paramref name="post"/> sends a
/// message to all other tabs, <see cref="Receive"/> takes theirs.
/// </summary>
public sealed class TabExchange(RemoteFiles files, Action<string> post)
{
    private readonly Dictionary<int, Action<TabMessage>> _pending = [];
    private readonly object _lock = new();
    private int _nextRequest;

    public string TabId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>This tab's document for the others (name for the list and the document), or null if it has none.</summary>
    public Func<(string Name, EditableDocument Document)?>? CurrentDocument { get; set; }

    /// <summary>Asks all other tabs for their documents and collects the answers that come within <paramref name="wait"/>.</summary>
    public async Task<IReadOnlyList<TabDocument>> OtherDocumentsAsync(TimeSpan wait, CancellationToken token = default)
    {
        var found = new List<TabDocument>();
        var request = Register(m =>
        {
            if (m.Kind == "document" && m.Name != null)
                lock (found)
                    if (found.All(d => d.TabId != m.From))
                        found.Add(new TabDocument(m.From, m.Name, m.PageCount, m.IsEdited));
        });
        try
        {
            Send(new TabMessage("list", TabId, null, request));
            await Task.Delay(wait, token).ConfigureAwait(false);
        }
        finally
        {
            Unregister(request);
        }
        lock (found)
            return [.. found];
    }

    /// <summary>
    /// Has the tab of <paramref name="document"/> write its document on the server and returns the new file's virtual
    /// path (for <see cref="RemotePdfBackend.OpenAsync"/>); null if the tab is gone, could not write it or took longer than
    /// <paramref name="timeout"/>.
    /// </summary>
    public async Task<string?> CopyAsync(TabDocument document, TimeSpan timeout, CancellationToken token = default)
    {
        var answer = new TaskCompletionSource<RemoteFileInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Register(m =>
        {
            if (m.Kind == "copied" && m.From == document.TabId)
                answer.TrySetResult(m.File);
        });
        try
        {
            Send(new TabMessage("copy", TabId, document.TabId, request));
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancel.CancelAfter(timeout);
            using (cancel.Token.Register(() => answer.TrySetResult(null)))
            {
                var file = await answer.Task.ConfigureAwait(false);
                return file == null ? null : files.Add(file);
            }
        }
        finally
        {
            Unregister(request);
        }
    }

    /// <summary>A message of another tab (from the transport).</summary>
    public void Receive(string json)
    {
        TabMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(json, RemoteJson.Default.TabMessage);
        }
        catch (JsonException)
        {
            return;
        }
        if (message == null || message.From == TabId || (message.To != null && message.To != TabId))
            return;
        switch (message.Kind)
        {
            case "list":
                if (CurrentDocument?.Invoke() is { } current && current.Document.PageCount > 0)
                    Send(new TabMessage("document", TabId, message.From, message.Request, current.Name, current.Document.PageCount,
                        current.Document.IsDirty));
                break;
            case "copy":
                _ = AnswerCopyAsync(message);
                break;
            default:
                Action<TabMessage>? handler;
                lock (_lock)
                    _pending.TryGetValue(message.Request, out handler);
                if (handler != null)
                    handler(message);
                else if (message is { Kind: "copied", File: { } late })
                    _ = files.Api.DeleteAsync(late.Id); // the asking tab gave up waiting: nobody will open the file
                break;
        }
    }

    private async Task AnswerCopyAsync(TabMessage request)
    {
        RemoteFileInfo? file = null;
        if (CurrentDocument?.Invoke() is { } current && current.Document.PageCount > 0)
        {
            try
            {
                var name = current.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? current.Name : current.Name + ".pdf";
                var document = current.Document;
                file = await files.Api.BuildAsync(new BuildRequest(RemotePdfBackend.IdOf(document.BaseSource), name,
                    RemotePdfBackend.ToRemote(document.Pages)), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or RemoteApiException or NotSupportedException or JsonException
                or TaskCanceledException)
            {
            }
        }
        Send(new TabMessage("copied", TabId, request.From, request.Request, File: file));
    }

    private int Register(Action<TabMessage> handler)
    {
        lock (_lock)
        {
            var request = ++_nextRequest;
            _pending[request] = handler;
            return request;
        }
    }

    private void Unregister(int request)
    {
        lock (_lock)
            _pending.Remove(request);
    }

    private void Send(TabMessage message) => post(JsonSerializer.Serialize(message, RemoteJson.Default.TabMessage));
}
