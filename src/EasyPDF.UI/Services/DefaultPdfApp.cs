using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using EasyPDF.Core;
using EasyPDF.Core.Settings;
using Microsoft.Win32;

namespace EasyPDF.App.Services;

public enum DefaultAppResult
{
    /// <summary>The program opens PDF files now (Linux, macOS).</summary>
    Done,

    /// <summary>Windows does not let programs make themselves the default: its settings page was opened instead.</summary>
    SettingsOpened,

    /// <summary>Only the installed program can be the default (not when run from the build output or an unpacked archive).</summary>
    NotAvailable,

    Failed,
}

/// <summary>Making the program the default for PDF files (Settings button, question at the first start).</summary>
public interface IDefaultPdfApp
{
    /// <summary>True if the program opens PDF files by default; null if that cannot be told.</summary>
    bool? IsDefault();

    Task<DefaultAppResult> MakeDefaultAsync();
}

/// <summary>
/// The operating system's default program for PDF files.
/// Windows: the MSI registers the program for .pdf (ProgId "&lt;name&gt;.Document", RegisteredApplications); programs may not
/// set themselves as default, so the settings page "Default apps" of the program is opened.
/// Linux: per user with xdg-mime, for the desktop entry of the .deb (&lt;package&gt;.desktop).
/// macOS: LaunchServices, for the bundle identifier of the running .app.
/// </summary>
public sealed class SystemDefaultPdfApp : IDefaultPdfApp
{
    public static SystemDefaultPdfApp Instance { get; } = new();

    /// <summary>ProgId the MSI registers (Package.wxs).</summary>
    public static string WindowsProgId => AppInfo.Name + ".Document";

    /// <summary>Name of the .deb package and its desktop entry: the shown name in lower case (as in build_linux.py).</summary>
    public static string LinuxPackageName =>
        new([.. AppInfo.Name.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '.' or '-')]);

    public bool? IsDefault()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsIsDefault();
            if (OperatingSystem.IsLinux())
                return LinuxDesktopFile() == null ? null : RunXdgMime("query", "default", "application/pdf").Output?.Trim() == LinuxPackageName + ".desktop";
            if (OperatingSystem.IsMacOS())
                return MacDefaults.IsDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                                       System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return null;
    }

    public Task<DefaultAppResult> MakeDefaultAsync() => Task.Run(() =>
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsOpenSettings();
            if (OperatingSystem.IsLinux())
            {
                var desktop = LinuxDesktopFile();
                if (desktop == null)
                    return DefaultAppResult.NotAvailable;
                return RunXdgMime("default", Path.GetFileName(desktop), "application/pdf").ExitCode == 0
                    ? DefaultAppResult.Done
                    : DefaultAppResult.Failed;
            }
            if (OperatingSystem.IsMacOS())
                return MacDefaults.MakeDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                                       System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return DefaultAppResult.Failed;
    });

    [SupportedOSPlatform("windows")]
    private static bool WindowsIsDefault()
    {
        // The user's choice for .pdf; newer Windows 11 versions keep it in UserChoiceLatest
        const string exts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\";
        foreach (var key in new[] { exts + @"UserChoiceLatest\ProgId", exts + "UserChoiceLatest", exts + "UserChoice" })
        {
            using var choice = Registry.CurrentUser.OpenSubKey(key);
            if (choice?.GetValue("ProgId") is string progId)
                return string.Equals(progId, WindowsProgId, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static DefaultAppResult WindowsOpenSettings()
    {
        // With the MSI's registration the page of the program opens (one click on "Set default"), otherwise the overview
        using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications");
        if (registered?.GetValue(AppInfo.Name) == null)
            return DefaultAppResult.NotAvailable;
        Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(AppInfo.Name)}")
        {
            UseShellExecute = true,
        })?.Dispose();
        return DefaultAppResult.SettingsOpened;
    }

    /// <summary>The installed desktop entry of the program; null if there is none (e.g. run from the .tar.gz).</summary>
    private static string? LinuxDesktopFile()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var dirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);
        return dirs.Prepend(home)
            .Select(dir => Path.Combine(dir, "applications", LinuxPackageName + ".desktop"))
            .FirstOrDefault(File.Exists);
    }

    private static (int ExitCode, string? Output) RunXdgMime(params string[] args)
    {
        var info = new ProcessStartInfo("xdg-mime") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
        if (process == null)
            return (-1, null);
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
            return (-1, null);
        }
        return (process.ExitCode, output);
    }
}

/// <summary>Default handler for PDF (UTI com.adobe.pdf) through LaunchServices, for the running .app.</summary>
[SupportedOSPlatform("macos")]
internal static class MacDefaults
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const uint Utf8 = 0x08000100;      // kCFStringEncodingUTF8
    private const uint RolesAll = 0xFFFFFFFF;  // kLSRolesAll

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringGetCString(IntPtr text, byte[] buffer, long size, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr handle);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFBundleGetMainBundle();

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFBundleGetIdentifier(IntPtr bundle);   // not owned

    [DllImport(CoreServices)]
    private static extern IntPtr LSCopyDefaultRoleHandlerForContentType(IntPtr contentType, uint role);

    [DllImport(CoreServices)]
    private static extern int LSSetDefaultRoleHandlerForContentType(IntPtr contentType, uint role, IntPtr handlerBundleId);

    /// <summary>Bundle id of the running app; null when not started as an .app (dotnet run).</summary>
    private static string? OwnBundleId()
    {
        var bundle = CFBundleGetMainBundle();
        return bundle == IntPtr.Zero ? null : Text(CFBundleGetIdentifier(bundle));
    }

    private static string? Text(IntPtr text)
    {
        if (text == IntPtr.Zero)
            return null;
        var buffer = new byte[512];
        if (!CFStringGetCString(text, buffer, buffer.Length, Utf8))
            return null;
        var value = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Array.IndexOf(buffer, (byte)0)));
        return value.Length > 0 ? value : null;
    }

    public static bool? IsDefault()
    {
        var own = OwnBundleId();
        if (own == null)
            return null;
        var type = CFStringCreateWithCString(IntPtr.Zero, "com.adobe.pdf", Utf8);
        try
        {
            var handler = LSCopyDefaultRoleHandlerForContentType(type, RolesAll);
            try
            {
                return string.Equals(Text(handler), own, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (handler != IntPtr.Zero)
                    CFRelease(handler);
            }
        }
        finally
        {
            CFRelease(type);
        }
    }

    public static DefaultAppResult MakeDefault()
    {
        var own = OwnBundleId();
        if (own == null)
            return DefaultAppResult.NotAvailable;
        var type = CFStringCreateWithCString(IntPtr.Zero, "com.adobe.pdf", Utf8);
        var id = CFStringCreateWithCString(IntPtr.Zero, own, Utf8);
        try
        {
            return LSSetDefaultRoleHandlerForContentType(type, RolesAll, id) == 0 ? DefaultAppResult.Done : DefaultAppResult.Failed;
        }
        finally
        {
            CFRelease(id);
            CFRelease(type);
        }
    }
}

/// <summary>
/// Linux and macOS have no installer that could ask: the program asks once, at the first start, whether it should open PDF
/// files (only if it can and does not already). On Windows the MSI asks.
/// </summary>
public static class DefaultAppPrompt
{
    /// <summary>Asks once; returns true if the question was shown.</summary>
    /// <param name="ask">Shows the question; true = yes.</param>
    public static async Task<bool> AskOnceAsync(AppSettings settings, IDefaultPdfApp app, Func<Task<bool>> ask, bool isWindows)
    {
        if (isWindows || settings.DefaultAppAsked || app.IsDefault() != false)
            return false;
        settings.DefaultAppAsked = true;
        if (await ask())
            await app.MakeDefaultAsync();
        return true;
    }
}
