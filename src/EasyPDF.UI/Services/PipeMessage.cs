using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace EasyPDF.App.Services;

/// <summary>
/// One message of text lines over a named pipe, confirmed by the receiver: the sender writes the lines and an empty
/// line, the receiver answers with one byte once it has read them. Only that answer counts as delivered.
/// Needed on Linux and macOS, where .NET builds named pipes on Unix sockets: there a client can connect to the
/// listening socket's backlog and write its lines although the server never accepts it (it closes its pipe after the
/// previous client); without the answer the sender would think its message arrived. Lines must not be empty.
/// </summary>
public static class PipeMessage
{
    private const byte Received = 1;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(7);   // the receiver may be between two connections
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Sends <paramref name="lines"/> to the server <paramref name="name"/>; false if nobody confirmed them in time.</summary>
    public static bool Send(string name, IEnumerable<string> lines, PipeOptions options = PipeOptions.None)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Patience)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, options | PipeOptions.Asynchronous);
                client.Connect(200);
                using (var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true))
                {
                    foreach (var line in lines)
                        writer.WriteLine(line);
                    writer.WriteLine();
                }
                var answer = new byte[1];
                using var wait = new CancellationTokenSource(AnswerTimeout);
                if (client.ReadAsync(answer, wait.Token).AsTask().GetAwaiter().GetResult() == 1 && answer[0] == Received)
                    return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
            {
                // Not accepted (yet): try again
            }
            Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>
    /// Reads one message from a connected server and confirms it; null if the sender broke off before its end
    /// (it then sends again).
    /// </summary>
    public static List<string>? Receive(NamedPipeServerStream server)
    {
        var lines = new List<string>();
        using (var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true))
        {
            while (true)
            {
                var line = reader.ReadLine();
                if (line == null)
                    return null;
                if (line.Length == 0)
                    break;
                lines.Add(line);
            }
        }
        server.WriteByte(Received);
        server.Flush();
        return lines;
    }
}
