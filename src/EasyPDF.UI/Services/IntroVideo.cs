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

    /// <summary>The program comes with a video (any language).</summary>
    public static bool IsAvailable { get; } = Find(Folder, "en") != null;

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
