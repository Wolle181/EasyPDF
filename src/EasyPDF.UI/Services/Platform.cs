using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Platform.Storage;

namespace EasyPDF.App.Services;

/// <summary>
/// Turns files the user picked or dropped into paths the program works with. The desktop program uses the local paths;
/// the web version uploads the file to the server and gets a virtual path for it.
/// </summary>
public interface IPlatformFiles
{
    /// <summary>A path <see cref="EasyPDF.Core.Pdfium.PdfBackend"/> can open for the file, or null.</summary>
    Task<string?> ToOpenPathAsync(IStorageFile file);

    /// <summary>The path to write to for a file chosen in the save dialog, or null.</summary>
    string? ToSavePath(IStorageFile file);

    /// <summary>Writes a file to a path from <see cref="ToSavePath"/> (web version: to the target the user chose).</summary>
    Task WriteAsync(string path, byte[] content);
}

/// <summary>Local files (desktop): their own paths.</summary>
public sealed class LocalPlatformFiles : IPlatformFiles
{
    public Task<string?> ToOpenPathAsync(IStorageFile file) => Task.FromResult(file.TryGetLocalPath());

    public string? ToSavePath(IStorageFile file) => file.TryGetLocalPath();

    public Task WriteAsync(string path, byte[] content) => System.IO.File.WriteAllBytesAsync(path, content);
}

/// <summary>What the program runs on: the desktop program, or the web version with the subset of features it offers.</summary>
public sealed class PlatformFeatures
{
    private readonly Func<ViewModels.MainViewModel, ICommand, bool>? _available;

    private PlatformFeatures(bool isWeb, Func<ViewModels.MainViewModel, ICommand, bool>? available)
    {
        IsWeb = isWeb;
        _available = available;
    }

    public static PlatformFeatures Desktop { get; } = new(false, null);

    /// <summary>The web version; <paramref name="available"/> says which commands it offers (the others are hidden).</summary>
    public static PlatformFeatures Web(Func<ViewModels.MainViewModel, ICommand, bool> available) => new(true, available);

    /// <summary>Set once at start-up by the web version.</summary>
    public static PlatformFeatures Current { get; set; } = Desktop;

    /// <summary>The file conversion of the platform (set by the web version at start-up).</summary>
    public static IPlatformFiles Files { get; set; } = new LocalPlatformFiles();

    public bool IsWeb { get; }

    /// <summary>Whether <paramref name="command"/> (a command of <paramref name="viewModel"/>) is offered here.</summary>
    /// <remarks>Buttons without a command (stamp gallery, templates, text style, recent files) are not offered on the web yet.</remarks>
    public bool IsAvailable(ViewModels.MainViewModel viewModel, ICommand? command) =>
        _available == null || (command != null && _available(viewModel, command));
}
