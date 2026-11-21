using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace EasyPDF.App.Services;

/// <summary>
/// The introduction video of the brand: branding/&lt;brand&gt;/videos/introduction.&lt;language&gt;.mp4, copied to videos/ next
/// to the program. The button in the brand header plays it with the system's video player; without videos it is hidden.
/// </summary>
public static class IntroVideo
{
    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "videos");

    private static readonly bool LocalAvailable = Find(Folder, "en") != null;

    /// <summary>The program comes with a video (any language).</summary>
    public static bool IsAvailable => Web != null ? Web.Languages.Count > 0 : LocalAvailable;

    /// <summary>
    /// Web version: the videos the server offers (languages) and how the page plays one (an overlay with a video element);
    /// null on the desktop.
    /// </summary>
    public static WebVideos? Web { get; set; }

    public sealed record WebVideos(System.Collections.Generic.IReadOnlyList<string> Languages, Func<string, System.Threading.Tasks.Task> Play)
    {
        /// <summary>The language of the user interface if there is a video in it, else English, else any.</summary>
        public string? Pick(string language) =>
            Languages.Contains(language) ? language : Languages.Contains("en") ? "en" : Languages.FirstOrDefault();
    }

    /// <summary>The video in the language of the user interface, or another one.</summary>
    public static string? Current => Find(Folder, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    /// <summary>The video in <paramref name="language"/>, else the English one, else any; null if there is none.</summary>
    public static string? Find(string folder, string language)
    {
        foreach (var lang in new[] { language, "en" })
        {
            var file = Path.Combine(folder, $"introduction.{lang}.mp4");
            if (File.Exists(file))
                return file;
        }
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "introduction.*.mp4").Order(StringComparer.Ordinal).FirstOrDefault() : null;
    }
}
