using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyPDF.Core.Layout;

namespace EasyPDF.Core.Settings;

public enum ThemeSetting
{
    System,
    Light,
    Dark,
}

/// <summary>User settings, stored as JSON in the per-user application data folder.</summary>
public sealed class AppSettings
{
    public const int MaxRecentFiles = 10;


    public ThemeSetting Theme { get; set; } = ThemeSetting.System;

    /// <summary>"" = follow the system, otherwise a culture name such as "en" or "de".</summary>
    public string Language { get; set; } = "";

    /// <summary>Stamp of the installer choice (language/theme) last taken over; see <see cref="ApplyInstallerChoice"/>.</summary>
    public string InstallerStamp { get; set; } = "";

    public List<string> RecentFiles { get; set; } = [];

    /// <summary>Folder with the stamp pictures (PNG); "" = Documents/&lt;app name&gt;/Stamps.</summary>
    public string StampFolder { get; set; } = "";

    /// <summary>Pen colour of inserted text as #RRGGBB.</summary>
    public string TextColor { get; set; } = TextStyle.DefaultColor;

    /// <summary>Font of inserted text: one of <see cref="TextStyle.Fonts"/>.</summary>
    public string TextFont { get; set; } = TextStyle.Fonts[0];

    /// <summary>Size of inserted text in points.</summary>
    public double TextFontSize { get; set; } = 12;

    /// <summary>Folder with the text templates (one .txt file each); "" = Documents/&lt;app name&gt;/Templates.</summary>
    public string TemplateFolder { get; set; } = "";

    /// <summary>
    /// Samples already put into the stamp and templates folders ("stamp:&lt;file&gt;", "template:&lt;id&gt;"), so deleted ones
    /// don't come back and new ones of a later version still arrive (see <c>StampLibrary.ProvideSamples</c>).
    /// </summary>
    public List<string> ProvidedSamples { get; set; } = [];

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    public bool RibbonCollapsed { get; set; }

    /// <summary>Linux/macOS: the question whether the program should open PDF files was shown (once, at the first start).</summary>
    public bool DefaultAppAsked { get; set; }

    /// <summary>Windows/Linux: show the menu bar in the window, off by default (macOS always has its global menu bar).</summary>
    public bool ShowMenuBar { get; set; }

    /// <summary>Ask before saving over the document's own file when it has boxes to black out or white out (their content is then gone for good).</summary>
    public bool WarnOverwriteRedacted { get; set; } = true;
    public bool PageSidebarVisible { get; set; } = true;

    public const double DefaultPageSidebarWidth = 125;

    /// <summary>Width of the page sidebar of the reading view in px (its thumbnails grow with it).</summary>
    public double PageSidebarWidth { get; set; } = DefaultPageSidebarWidth;
    public ZoomMode ZoomMode { get; set; } = ZoomMode.FitHeight;
    public double CustomZoomPercent { get; set; } = 100;
    public double ThumbnailZoomPercent { get; set; } = 100;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name, "settings.json");

    /// <summary>
    /// The file these settings were loaded from, where <see cref="Save"/> writes without a path. Null for settings made
    /// in code (tests): they are never written to the user's settings file.
    /// </summary>
    [JsonIgnore]
    public string? FilePath { get; private set; }

    /// <summary>Loads the settings; a missing or broken file yields defaults (saved to that file later).</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        AppSettings? settings = null;
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJson.Default.AppSettings);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        settings ??= new AppSettings();
        settings.FilePath = path;
        return settings;
    }

    /// <summary>Writes the settings to <paramref name="path"/>, or to the file they were loaded from; does nothing without either.</summary>
    public void Save(string? path = null)
    {
        path ??= FilePath;
        if (path == null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Takes over the language and theme chosen in the installer, once per installer run: the installer writes a new
    /// <paramref name="stamp"/> whenever its dialog was shown, so a later change in the app's settings sticks until the next install.
    /// Language: "system", "en" or "de"; theme: a <see cref="ThemeSetting"/> name. Unknown values are ignored.
    /// </summary>
    /// <returns>True if the settings changed (and should be saved).</returns>
    public bool ApplyInstallerChoice(string? language, string? theme, string? stamp)
    {
        if (string.IsNullOrEmpty(stamp) || stamp == InstallerStamp)
            return false;
        InstallerStamp = stamp;
        var lang = language?.Trim().ToLowerInvariant();
        if (lang == "system")
            Language = "";
        else if (lang is "en" or "de")
            Language = lang;
        if (Enum.TryParse<ThemeSetting>(theme?.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            Theme = parsed;
        return true;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Moves the file to the top of the recent list (max. <see cref="MaxRecentFiles"/>).</summary>
    public void AddRecentFile(string path)
    {
        path = Path.GetFullPath(path);
        RecentFiles = [.. RecentFiles.Where(p => !PathComparer.Equals(p, path)).Prepend(path).Take(MaxRecentFiles)];
    }

    public void RemoveRecentFile(string path) =>
        RecentFiles = [.. RecentFiles.Where(p => !PathComparer.Equals(p, path))];

    public void ClearRecentFiles() => RecentFiles = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
