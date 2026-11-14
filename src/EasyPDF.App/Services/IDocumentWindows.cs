using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Services;

/// <summary>
/// The open main windows, one document each. A window without a document takes the next file itself; a window with one
/// opens further files in a new window. Without this service (tests) a view model replaces its document as before.
/// </summary>
public interface IDocumentWindows
{
    int Count { get; }

    /// <summary>The area copied with the copy-area tool, the same for all windows.</summary>
    AreaClipboard AreaClipboard { get; }

    /// <summary>Opens an empty window.</summary>
    void OpenNewWindow();

    /// <summary>Brings the window whose document is the file <paramref name="path"/> to the front; false if there is none.</summary>
    bool ActivateWindowShowing(string path);

    /// <summary>True if the document of another window than <paramref name="asking"/> reads from the file <paramref name="path"/>.</summary>
    bool IsFileUsedByOtherWindow(string path, MainViewModel asking);

    /// <summary>The documents of all windows except <paramref name="asking"/>, most recently active first.</summary>
    IReadOnlyList<OpenDocumentInfo> OtherDocuments(MainViewModel asking);

    /// <summary>Opens a new window next to <paramref name="from"/> and lets <paramref name="load"/> put a document into it.</summary>
    Task OpenInNewWindowAsync(MainViewModel from, Func<MainViewModel, Task> load);

    /// <summary>Closes all windows (asking about unsaved changes window by window; stops when one is cancelled).</summary>
    Task CloseAllAsync();
}
