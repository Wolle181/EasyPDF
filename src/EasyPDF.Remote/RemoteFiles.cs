using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EasyPDF.Remote;

/// <summary>
/// The files this browser tab has on the server, by virtual path. The user interface works with paths (as for local
/// files); a path <c>/remote/&lt;id&gt;/&lt;name&gt;</c> stands for an uploaded or server-made file. Each opened
/// <see cref="RemotePdfSource"/> holds a reference; when the last one is disposed the file is deleted on the server.
/// </summary>
public sealed class RemoteFiles(RemoteApi api)
{
    public const string Prefix = "/remote/";

    private readonly Dictionary<string, RemoteFileInfo> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _references = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public RemoteApi Api { get; } = api;

    public static string PathOf(RemoteFileInfo info) => $"{Prefix}{info.Id}/{info.Name}";

    public static bool IsRemotePath(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Registers a file the server just made or received; returns its virtual path.</summary>
    public string Add(RemoteFileInfo info)
    {
        var path = PathOf(info);
        lock (_lock)
            _byPath[path] = info;
        return path;
    }

    public bool TryGet(string path, out RemoteFileInfo info)
    {
        lock (_lock)
            return _byPath.TryGetValue(path, out info!);
    }

    /// <summary>A new source for a registered file (one more reference).</summary>
    public RemotePdfSource Open(RemoteFileInfo info)
    {
        lock (_lock)
        {
            _byPath[PathOf(info)] = info;
            _references[info.Id] = _references.GetValueOrDefault(info.Id) + 1;
        }
        return new RemotePdfSource(this, info);
    }

    internal void Release(RemoteFileInfo info)
    {
        bool last;
        lock (_lock)
        {
            var count = _references.GetValueOrDefault(info.Id) - 1;
            last = count <= 0;
            if (last)
            {
                _references.Remove(info.Id);
                _byPath.Remove(PathOf(info));
            }
            else
                _references[info.Id] = count;
        }
        if (last)
            _ = Task.Run(() => Api.DeleteAsync(info.Id));
    }
}
