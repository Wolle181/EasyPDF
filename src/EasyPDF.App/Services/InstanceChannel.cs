using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;

namespace EasyPDF.App.Services;

/// <summary>
/// One running program per user: the first process owns a named mutex and listens on a named pipe; later starts
/// (double-click in the file manager, context menu, a second start without files) hand their arguments over and exit,
/// and the first process opens them in one of its windows. The request is the command line itself, one argument per line.
/// </summary>
public sealed class InstanceChannel : IDisposable
{
    public static string DefaultName => $"{EasyPDF.Core.AppInfo.Name}.Instance.{Environment.UserName}";   // per brand: brands can run side by side

    private readonly string _name;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<StartupArguments> _pending = [];
    private Action<StartupArguments>? _handler;

    private InstanceChannel(string name, Mutex mutex)
    {
        _name = name;
        _mutex = mutex;
        new Thread(Listen) { IsBackground = true, Name = "Instance channel" }.Start();
    }

    /// <summary>
    /// Becomes the first instance (returns the channel, which listens from now on) or hands <paramref name="request"/>
    /// to the running one (returns null). <paramref name="handedOver"/> true: this process should exit; false with null:
    /// the running instance did not answer, this process runs on its own.
    /// </summary>
    public static InstanceChannel? Start(string name, StartupArguments request, out bool handedOver)
    {
        var mutex = new Mutex(true, name, out var isFirst);
        if (isFirst)
        {
            handedOver = false;
            return new InstanceChannel(name, mutex);
        }
        mutex.Dispose();
        handedOver = TrySend(name, request);
        return null;
    }

    /// <summary>
    /// Receives the requests on the UI side; requests that arrived before (while the UI was starting) are delivered
    /// right away. The handler is called on the listening thread.
    /// </summary>
    public void SetHandler(Action<StartupArguments> handler)
    {
        StartupArguments[] early;
        lock (_pending)
        {
            _handler = handler;
            early = [.. _pending];
            _pending.Clear();
        }
        foreach (var request in early)
            handler(request);
    }

    private void Listen()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                server.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
                if (PipeMessage.Receive(server) is { } lines)
                    Deliver(StartupArguments.Parse(lines));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that broke off: wait for the next one
            }
        }
    }

    private void Deliver(StartupArguments request)
    {
        Action<StartupArguments>? handler;
        lock (_pending)
        {
            handler = _handler;
            if (handler == null)
                _pending.Add(request);
        }
        handler?.Invoke(request);
    }

    private static bool TrySend(string name, StartupArguments request)
    {
        // Explorer gave this process the foreground: let the running instance bring its window to the front
        if (OperatingSystem.IsWindows())
            AllowSetForegroundWindow(AsfwAny);

        // The running instance may be between two connections: PipeMessage retries for a few seconds
        var lines = new List<string>();
        if (request.Merge)
            lines.Add(StartupArguments.MergeOption);
        if (request.Read)
            lines.Add(StartupArguments.ReadOption);
        lines.AddRange(request.Files);
        return PipeMessage.Send(name, lines, PipeOptions.CurrentUserOnly);
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Stops listening and gives up the first-instance role (call on the thread that started the channel).</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread: the mutex is released when the process ends
        }
        _mutex.Dispose();
    }
}
