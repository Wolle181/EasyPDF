using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EasyPDF.Core.Settings;

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

    /// <summary>
    /// Web version: shows the pages (with everything placed on them) as a PDF in a new browser tab, where the browser's
    /// viewer prints it. False if the browser blocked the tab. The desktop prints itself and never calls this.
    /// </summary>
    Task<bool> ShowForPrintingAsync(IReadOnlyList<EasyPDF.Core.Documents.PageEntry> pages, string name);

    /// <summary>
    /// Web version: has the server write the document as it is now (like saving, with bookmarks) and returns a handle for
    /// <see cref="ShareAsync"/>. The desktop sends emails itself and never calls this.
    /// </summary>
    Task<string> PrepareForSharingAsync(EasyPDF.Core.Documents.EditableDocument document, string name);

    /// <summary>
    /// Web version: hands the prepared PDF to the system's share sheet (which offers the email program, among others).
    /// </summary>
    Task<ShareOutcome> ShareAsync(string prepared, string name);
}

/// <summary>How handing a file to the system's share sheet ended.</summary>
public enum ShareOutcome
{
    Shared,
    /// <summary>The user closed the share sheet.</summary>
    Cancelled,
    /// <summary>The browser wants a fresh click first (preparing took too long after the first one).</summary>
    NeedsClick,
}

/// <summary>Local files (desktop): their own paths.</summary>
public sealed class LocalPlatformFiles : IPlatformFiles
{
    public Task<string?> ToOpenPathAsync(IStorageFile file) => Task.FromResult(file.TryGetLocalPath());

    public string? ToSavePath(IStorageFile file) => file.TryGetLocalPath();

    public Task WriteAsync(string path, byte[] content) => System.IO.File.WriteAllBytesAsync(path, content);

    public Task<bool> ShowForPrintingAsync(IReadOnlyList<EasyPDF.Core.Documents.PageEntry> pages, string name) =>
        throw new NotSupportedException("The desktop program prints itself.");

    public Task<string> PrepareForSharingAsync(EasyPDF.Core.Documents.EditableDocument document, string name) =>
        throw new NotSupportedException("The desktop program sends emails itself.");

    public Task<ShareOutcome> ShareAsync(string prepared, string name) =>
        throw new NotSupportedException("The desktop program sends emails itself.");
}

/// <summary>What the program runs on: the desktop program, or the web version with the subset of features it offers.</summary>
/// <summary>
/// Where the web version keeps the user's stamps and templates: in the browser's storage instead of folders the user can
/// open. The folders exist in the program's memory as on the desktop; this class fills them at start and writes them back
/// after each change.
/// </summary>
public interface IUserLibrary
{
    /// <summary>At start, once the settings and language are applied: fills the stamp and templates folders (also with the samples).</summary>
    void Prepare(AppSettings settings);

    /// <summary>The user changed stamps or templates: keep them.</summary>
    void Changed();

    /// <summary>Whether the stamp at <paramref name="path"/> can be used but not changed (a shared stamp of the server).</summary>
    bool IsReadOnly(string path);

    /// <summary>Saves the user's stamps and templates as one ZIP file the user chooses.</summary>
    Task ExportAsync(TopLevel host);

    /// <summary>Adds the stamps and templates of a ZIP file the user chooses (made by <see cref="ExportAsync"/>); false if none was chosen or it held none.</summary>
    Task<bool> ImportAsync(TopLevel host);
}

public sealed class PlatformFeatures
{
    private readonly Func<ViewModels.MainViewModel, ICommand, bool>? _available;
    private readonly IReadOnlySet<string> _buttons;

    private PlatformFeatures(bool isWeb, Func<ViewModels.MainViewModel, ICommand, bool>? available, IReadOnlySet<string>? buttons = null)
    {
        IsWeb = isWeb;
        _available = available;
        _buttons = buttons ?? new HashSet<string>();
    }

    public static PlatformFeatures Desktop { get; } = new(false, null);

    /// <summary>The web version; <paramref name="available"/> says which commands it offers (the others are hidden).</summary>
    /// <param name="buttons">Names of the ribbon buttons without a command (they open a gallery or menu) that are offered too.</param>
    public static PlatformFeatures Web(Func<ViewModels.MainViewModel, ICommand, bool> available, IReadOnlySet<string>? buttons = null) =>
        new(true, available, buttons);

    /// <summary>Set once at start-up by the web version.</summary>
    public static PlatformFeatures Current { get; set; } = Desktop;

    /// <summary>Creates the text recognizer when the platform has its own (web version: recognition runs on the server).</summary>
    public static Func<EasyPDF.Core.Ocr.IOcrEngine>? OcrEngineFactory { get; set; }

    /// <summary>The file conversion of the platform (set by the web version at start-up).</summary>
    public static IPlatformFiles Files { get; set; } = new LocalPlatformFiles();

    /// <summary>The stamps and templates in the browser (web version); null on the desktop, where they are normal folders.</summary>
    public static IUserLibrary? Library { get; set; }

    public bool IsWeb { get; }

    /// <summary>Whether <paramref name="command"/> (a command of <paramref name="viewModel"/>) is offered here.</summary>
    /// <remarks>Buttons without a command (stamp gallery, templates, text style, recent files) are not offered on the web yet.</remarks>
    public bool IsAvailable(ViewModels.MainViewModel viewModel, ICommand? command) =>
        _available == null || (command != null && _available(viewModel, command));

    /// <summary>Whether a ribbon button without a command (by its name) is offered here.</summary>
    public bool IsButtonAvailable(string? name) => _available == null || (name != null && _buttons.Contains(name));
}
