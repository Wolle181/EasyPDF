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

    [JSImport("loadSettings", "interop")]
    public static partial string LoadSettings();

    [JSImport("saveSettings", "interop")]
    public static partial void SaveSettings(string json);
}
