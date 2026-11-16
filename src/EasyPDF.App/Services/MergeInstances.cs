using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;

namespace EasyPDF.App.Services;

/// <summary>
/// "Merge with &lt;app&gt;" on several selected files: Windows Explorer starts one process per file. The first
/// process becomes the collector (named mutex) and receives the files of the others over a named pipe; the others
/// hand over their files and exit, so a single Concatenate dialog lists all of them.
/// Runs before the UI starts and blocks (the mutex must be released by the thread that owns it).
/// </summary>
public static class MergeInstances
{
    public static string DefaultName => $"{EasyPDF.Core.AppInfo.Name}.Merge.{Environment.UserName}";   // per brand: brands can run side by side

    /// <summary>
    /// Returns all files to merge (sorted by file name) if this process is the collector, or null if the files were
    /// handed to another process (then this process should exit). The collector stops waiting when no further files
    /// arrived for <paramref name="quiet"/>.
    /// </summary>
    public static IReadOnlyList<string>? Collect(IReadOnlyList<string> ownFiles, string name, TimeSpan quiet)
    {
        using var mutex = new Mutex(true, name, out var isCollector);
        if (!isCollector)
            return TrySend(name, ownFiles) ? null : Sorted(ownFiles); // collector already gone: open the files here

        var files = new List<string>(ownFiles);
        try
        {
            while (true)
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                using var wait = new CancellationTokenSource(quiet);
                try
                {
                    server.WaitForConnectionAsync(wait.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    break; // nobody else for a while: done
                }
                try
                {
                    if (PipeMessage.Receive(server) is { } received)
                        files.AddRange(received);
                }
                catch (IOException)
                {
                    // A sender that broke off: it sends again
                }
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
        return Sorted(files);
    }

    // The collector may still be setting up its pipe (or be between two connections): PipeMessage retries
    private static bool TrySend(string name, IReadOnlyList<string> files) => PipeMessage.Send(name, files);

    private static IReadOnlyList<string> Sorted(IEnumerable<string> files)
    {
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        return [.. files.Distinct(comparer).OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase)];
    }
}
