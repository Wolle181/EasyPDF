using System.Linq;
using System.Reflection;

namespace EasyPDF.Core;

/// <summary>The product name, set once in the brand profile (<c>AppDisplayName</c> in branding/&lt;brand&gt;/Branding.props).</summary>
public static class AppInfo
{
    /// <summary>
    /// The name the user sees; also names the user's folders: settings (%AppData%/&lt;name&gt;), stamps
    /// (Documents/&lt;name&gt;/Stamps), templates (Documents/&lt;name&gt;/Templates), see <see cref="DocumentsFolder"/>, and temporary files.
    /// </summary>
    public static string Name { get; } =
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "AppDisplayName")?.Value
        ?? "EasyPDF";

    /// <summary>Folder for temporary files (blank pages, print jobs, mail attachments) below the system's temp folder.</summary>
    public static string TempFolder => System.IO.Path.Combine(System.IO.Path.GetTempPath(), Name);

    /// <summary>
    /// &lt;user folder&gt;/<paramref name="subfolder"/>: Documents/&lt;name&gt; (the home folder where there is no Documents folder), or
    /// the local application data folder where that can't be written, see <see cref="ChooseUserFolder"/>.
    /// </summary>
    public static string DocumentsFolder(string subfolder) => System.IO.Path.Combine(UserFolder.Value, subfolder);

    /// <summary>Decided once per program run: the check writes a file, and the folder must not change while the program runs.</summary>
    private static readonly System.Lazy<string> UserFolder = new(() =>
    {
        var documents = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
            documents = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData, System.Environment.SpecialFolderOption.DoNotVerify);
        return ChooseUserFolder(System.IO.Path.Combine(documents, Name), System.IO.Path.Combine(local, Name));
    });

    /// <summary>
    /// The folder for the user's own files: <paramref name="preferred"/> if it can be written. Windows can refuse that for
    /// a program it doesn't know ("Controlled folder access"), and a redirected Documents folder can be offline or locked by
    /// a policy; then <paramref name="fallback"/>, so stamps and templates still work. A fallback in use stays in use while
    /// the preferred folder doesn't exist, so the user's files don't seem lost once the block is lifted.
    /// </summary>
    public static string ChooseUserFolder(string preferred, string fallback)
    {
        if (System.IO.Directory.Exists(fallback) && !System.IO.Directory.Exists(preferred))
            return fallback;
        return IsWritable(preferred) || !IsWritable(fallback) ? preferred : fallback;
    }

    /// <summary>Creates the folder if needed and tries to write a file in it; many blocks only show when writing.</summary>
    private static bool IsWritable(string folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            var probe = System.IO.Path.Combine(folder, "." + System.Guid.NewGuid().ToString("N"));
            System.IO.File.WriteAllBytes(probe, []);
            System.IO.File.Delete(probe);
            return true;
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException or System.NotSupportedException)
        {
            return false;
        }
    }
}
