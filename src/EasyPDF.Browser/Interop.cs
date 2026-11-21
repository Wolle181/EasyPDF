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
}
