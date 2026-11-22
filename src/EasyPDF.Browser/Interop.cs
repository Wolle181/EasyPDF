using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace EasyPDF.Browser;

/// <summary>The page's JavaScript helpers (wwwroot/interop.js).</summary>
internal static partial class Interop
{
    public static Task LoadAsync() => JSHost.ImportAsync("interop", "../interop.js");

    [JSImport("removeSplash", "interop")]
    public static partial void RemoveSplash();

    [JSImport("setTitle", "interop")]
    public static partial void SetTitle(string title);

    /// <summary>Opens <paramref name="url"/> in a new tab; false if the browser blocked it.</summary>
    [JSImport("openTab", "interop")]
    public static partial bool OpenTab(string url);

    /// <summary>Downloads the file at <paramref name="url"/> (the server sends it as attachment: the page stays).</summary>
    [JSImport("download", "interop")]
    public static partial void Download(string url);

    /// <summary>The browser's operating system as it names it ("Windows", "macOS", "Linux", …; the user agent if unknown).</summary>
    [JSImport("platform", "interop")]
    public static partial string Platform();

    [JSImport("playVideo", "interop")]
    public static partial void PlayVideo(string url, string closeLabel);

    [JSImport("loadSettings", "interop")]
    public static partial string LoadSettings();

    [JSImport("saveSettings", "interop")]
    public static partial void SaveSettings(string json);

    /// <summary>The user's stamps and templates (a ZIP file as Base64) from the browser's storage; empty if there are none.</summary>
    [JSImport("loadLibrary", "interop")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> LoadLibrary();

    [JSImport("saveLibrary", "interop")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task SaveLibrary(string zipBase64);

    /// <summary>Whether the browser can hand a PDF file to the system's share sheet (Web Share API with files).</summary>
    [JSImport("canShareFiles", "interop")]
    public static partial bool CanShareFiles();

    /// <summary>
    /// Fetches the PDF at <paramref name="url"/> and hands it to the system's share sheet: "shared", "cancelled", or
    /// "activation" if the browser wants a fresh click first.
    /// </summary>
    [JSImport("shareFile", "interop")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> ShareFile(string url, string name, string title);

    /// <summary>
    /// Opens the channel to the other tabs of this site (BroadcastChannel) and calls <paramref name="receive"/> with each of
    /// their messages; false if the browser has none.
    /// </summary>
    [JSImport("openTabChannel", "interop")]
    public static partial bool OpenTabChannel([JSMarshalAs<JSType.Function<JSType.String>>] System.Action<string> receive);

    /// <summary>Sends a message to all other tabs of this site.</summary>
    [JSImport("postTabMessage", "interop")]
    public static partial void PostTabMessage(string message);

    /// <summary>Milliseconds since the page started loading.</summary>
    [JSImport("globalThis.performance.now")]
    public static partial double Now();

    /// <summary>A text kept in the site's IndexedDB under <paramref name="key"/>; empty if there is none.</summary>
    [JSImport("loadStored", "interop")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> LoadStored(string key);

    [JSImport("saveStored", "interop")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task SaveStored(string key, string value);
}
