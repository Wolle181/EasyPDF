using System;
using EasyPDF.App.Localization;
using EasyPDF.Core.Settings;
using Microsoft.Win32;

namespace EasyPDF.App.Services;

/// <summary>
/// Language and theme chosen in the Windows installer (MSI dialog "Preferences"), stored under
/// HKCU\Software\&lt;shown name&gt; (Language, Theme, PreferencesStamp; the MSI installs per user). The other platforms have no
/// installer dialog.
/// </summary>
public static class InstallerChoice
{
    /// <summary>The key the MSI writes (<c>Software\$(var.AppDisplayName)</c> in Package.wxs).</summary>
    public static string Key => @"Software\" + Loc.AppName;

    /// <summary>Takes the installer's choice over into <paramref name="settings"/> once per installer run; true if it changed.</summary>
    public static bool ApplyTo(AppSettings settings)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key == null)
                return false;
            return settings.ApplyInstallerChoice(key.GetValue("Language") as string, key.GetValue("Theme") as string,
                key.GetValue("PreferencesStamp") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }
}
