using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace EasyPDF.App.Services;

/// <summary>Hands a file to the user's email program as the attachment of a new message.</summary>
public interface IMailService
{
    /// <summary>
    /// Opens a new message with <paramref name="filePath"/> attached in the default email program. Throws if no email
    /// program could be reached. The task may only finish when the message window is closed (Windows), so callers should
    /// not hold the user up waiting for it.
    /// </summary>
    Task SendAsync(string filePath, string subject);
}

/// <summary>
/// Windows: Simple MAPI (the program set as default email app, e.g. Outlook or Thunderbird). macOS: the default
/// handler of mailto: links gets the file. Linux: <c>xdg-email</c>, which knows Thunderbird, Evolution, KMail and others.
/// </summary>
public sealed class PlatformMailService : IMailService
{
    public static PlatformMailService Instance { get; } = new();

    public Task SendAsync(string filePath, string subject)
    {
        if (OperatingSystem.IsWindows())
            return SimpleMapi.SendAsync(filePath, subject);
        if (OperatingSystem.IsMacOS())
            return MacMail.SendAsync(filePath, subject);
        return Task.Run(() => RunLauncher("xdg-email", "--subject", subject, "--attach", filePath));
    }

    /// <summary>
    /// Argument of Thunderbird's <c>-compose</c>: values in single quotes (commas inside are fine), the attachment as a
    /// file URL; a quote in the subject is dropped, in the URL it is escaped.
    /// </summary>
    public static string ThunderbirdCompose(string filePath, string subject) =>
        $"subject='{subject.Replace("'", "", StringComparison.Ordinal)}',attachment='{new Uri(filePath).AbsoluteUri.Replace("'", "%27", StringComparison.Ordinal)}'";

    /// <summary>
    /// Starts a program that opens the email window. Some of them (Thunderbird when it is not running yet) keep running
    /// until that window is closed: an exit code only counts if it comes within a few seconds.
    /// </summary>
    internal static void RunLauncher(string program, params string[] args)
    {
        // No redirected output: a program that keeps running must not hit a closed pipe later
        var info = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"“{program}” could not be started: {ex.Message}", ex);
        }
        if (process == null)
            throw new InvalidOperationException($"“{program}” could not be started.");
        using (process)
        {
            if (process.WaitForExit(5000) && process.ExitCode != 0)
                throw new InvalidOperationException($"“{program}” failed with exit code {process.ExitCode}.");
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class SimpleMapi
{
    private const uint MapiLogonUi = 0x1;
    private const uint MapiDialog = 0x8;
    private const uint SuccessSuccess = 0;
    private const uint MapiUserAbort = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiMessageW
    {
        public uint Reserved;
        public IntPtr Subject;
        public IntPtr NoteText;
        public IntPtr MessageType;
        public IntPtr DateReceived;
        public IntPtr ConversationId;
        public uint Flags;
        public IntPtr Originator;
        public uint RecipCount;
        public IntPtr Recips;
        public uint FileCount;
        public IntPtr Files;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiFileDescW
    {
        public uint Reserved;
        public uint Flags;
        public uint Position;
        public IntPtr PathName;
        public IntPtr FileName;
        public IntPtr FileType;
    }

    [DllImport("mapi32.dll", EntryPoint = "MAPISendMailW")]
    private static extern uint MapiSendMailW(IntPtr session, IntPtr uiParam, ref MapiMessageW message, uint flags, uint reserved);

    /// <summary>
    /// MAPI wants a single-threaded apartment and blocks until the message window is closed (Outlook), so it gets a
    /// thread of its own; the app stays usable meanwhile.
    /// </summary>
    public static Task SendAsync(string filePath, string subject)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Send(filePath, subject);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }) { IsBackground = true, Name = "MAPI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void Send(string filePath, string subject)
    {
        var path = Marshal.StringToHGlobalUni(filePath);
        var name = Marshal.StringToHGlobalUni(Path.GetFileName(filePath));
        var title = Marshal.StringToHGlobalUni(subject);
        var file = Marshal.AllocHGlobal(Marshal.SizeOf<MapiFileDescW>());
        try
        {
            Marshal.StructureToPtr(new MapiFileDescW { Position = uint.MaxValue, PathName = path, FileName = name }, file, false);
            var message = new MapiMessageW { Subject = title, FileCount = 1, Files = file };
            uint result;
            try
            {
                result = MapiSendMailW(IntPtr.Zero, IntPtr.Zero, ref message, MapiLogonUi | MapiDialog, 0);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new InvalidOperationException("No email program supporting Simple MAPI is installed.", ex);
            }
            if (result != SuccessSuccess && result != MapiUserAbort)
                throw new InvalidOperationException($"The email program reported MAPI error {result}.");
        }
        finally
        {
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(title);
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(path);
        }
    }
}

[SupportedOSPlatform("macos")]
internal static class MacMail
{
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringGetCString(IntPtr text, byte[] buffer, long size, uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr handle);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern IntPtr LSCopyDefaultHandlerForURLScheme(IntPtr scheme);

    public static Task SendAsync(string filePath, string subject) => Task.Run(() =>
    {
        var bundleId = DefaultMailApp() ?? "com.apple.mail";
        // Thunderbird opens a file it is given instead of attaching it; its command line can write a message
        if (bundleId.Contains("thunderbird", StringComparison.OrdinalIgnoreCase) || bundleId.Contains("betterbird", StringComparison.OrdinalIgnoreCase))
        {
            var app = AppPath(bundleId);
            var binary = app == null ? null : Path.Combine(app, "Contents", "MacOS", "thunderbird");
            if (binary != null && File.Exists(binary))
            {
                PlatformMailService.RunLauncher(binary, "-compose", PlatformMailService.ThunderbirdCompose(filePath, subject));
                return;
            }
        }
        // Mail, Outlook, Spark …: opening the file with them starts a new message with it attached
        PlatformMailService.RunLauncher("open", "-b", bundleId, filePath);
    });

    /// <summary>Bundle id of the app that handles mailto: links (set in Mail ▸ Settings ▸ Default email reader); null if unknown.</summary>
    private static string? DefaultMailApp()
    {
        try
        {
            var scheme = CFStringCreateWithCString(IntPtr.Zero, "mailto", Utf8);
            if (scheme == IntPtr.Zero)
                return null;
            try
            {
                var handler = LSCopyDefaultHandlerForURLScheme(scheme);
                if (handler == IntPtr.Zero)
                    return null;
                try
                {
                    var buffer = new byte[512];
                    if (!CFStringGetCString(handler, buffer, buffer.Length, Utf8))
                        return null;
                    var text = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Array.IndexOf(buffer, (byte)0)));
                    return text.Length > 0 ? text : null;
                }
                finally
                {
                    CFRelease(handler);
                }
            }
            finally
            {
                CFRelease(scheme);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // the (deprecated) Launch Services call is gone
        }
    }

    private static string? AppPath(string bundleId)
    {
        try
        {
            var info = new ProcessStartInfo("osascript") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add($"POSIX path of (path to application id \"{bundleId}\")");
            using var process = Process.Start(info);
            if (process == null)
                return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
