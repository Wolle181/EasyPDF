using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Stamps;
using EasyPDF.Core.Templates;

namespace EasyPDF.Browser;

/// <summary>
/// The user's stamps and templates in the browser: kept as one ZIP file (<see cref="LibraryArchive"/>) in the site's
/// IndexedDB, unpacked into the program's stamp and templates folders at start and packed again after each change. The
/// server adds its sample stamps and the shared stamps and templates of the installation; the shared ones are not kept
/// (they come fresh from the server each time) and can't be changed or deleted.
/// </summary>
internal sealed class BrowserLibrary : IUserLibrary
{
    private readonly byte[]? _stored;
    private readonly byte[]? _server;
    private readonly HashSet<string> _sharedFolders = new(StringComparer.Ordinal);

    /// <summary>Shared templates put into the templates folder (file → text): kept only once the user changed them.</summary>
    private readonly Dictionary<string, string> _sharedTemplates = new(StringComparer.Ordinal);

    private string _stampFolder = StampLibrary.DefaultFolder;
    private string _templateFolder = TemplateLibrary.DefaultFolder;

    private BrowserLibrary(byte[]? stored, byte[]? server)
    {
        _stored = stored;
        _server = server;
    }

    /// <summary>Reads the stored library and the server's (both may be missing: the program then starts with what it has).</summary>
    public static async Task<BrowserLibrary> LoadAsync(HttpClient http)
    {
        byte[]? stored = null;
        try
        {
            var text = await Interop.LoadLibrary();
            if (text.Length > 0)
                stored = Convert.FromBase64String(text);
        }
        catch (Exception ex) when (ex is FormatException or System.Runtime.InteropServices.JavaScript.JSException)
        {
        }
        byte[]? server = null;
        try
        {
            server = await http.GetByteArrayAsync("api/library");
        }
        catch (HttpRequestException)
        {
        }
        return new BrowserLibrary(stored, server);
    }

    public void Prepare(AppSettings settings)
    {
        _stampFolder = StampLibrary.ResolveFolder(settings);
        _templateFolder = TemplateLibrary.ResolveFolder(settings);
        if (_stored != null)
            TryExtract(() =>
            {
                LibraryArchive.Extract(_stored, LibraryArchive.Stamps, _stampFolder, ".png", subfolders: true);
                LibraryArchive.Extract(_stored, LibraryArchive.Templates, _templateFolder, TemplateLibrary.Extension, subfolders: false);
            });
        if (_server == null)
            return;
        TryExtract(() =>
        {
            // The samples go where the desktop program has them; SampleContent copies them into the folder as there
            LibraryArchive.Extract(_server, "samples/", StampLibrary.SampleFolder, ".png", subfolders: true);

            // Shared stamps: a section "Shared" in the gallery, one more for each subfolder ("Shared – Signatures")
            var shared = Loc.T("StampFolderShared");
            AddShared("shared/stamps/", Path.Combine(_stampFolder, shared));
            foreach (var sub in LibraryArchive.Subfolders(_server, "shared/stamps/"))
                AddShared($"shared/stamps/{sub}/", Path.Combine(_stampFolder, $"{shared} – {sub}"));

            // Shared templates next to the user's own, unless the user has one of the same name
            var temporary = Path.Combine(Path.GetTempPath(), "shared-templates");
            LibraryArchive.Extract(_server, "shared/templates/", temporary, TemplateLibrary.Extension, subfolders: false);
            foreach (var template in TemplateLibrary.Load(temporary))
            {
                var target = Path.Combine(_templateFolder, Path.GetFileName(template.FilePath!));
                if (File.Exists(target))
                    continue;
                Directory.CreateDirectory(_templateFolder);
                File.Copy(template.FilePath!, target);
                _sharedTemplates[target] = File.ReadAllText(target);
            }
        });
    }

    private void AddShared(string prefix, string folder)
    {
        if (LibraryArchive.Extract(_server!, prefix, folder, ".png", subfolders: false) > 0)
            _sharedFolders.Add(Path.GetFullPath(folder));
    }

    private static void TryExtract(Action extract)
    {
        try
        {
            extract();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Damaged storage or ZIP: the program starts with what could be read
        }
    }

    public bool IsReadOnly(string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder && _sharedFolders.Contains(folder);

    /// <summary>The user's own stamps and templates: without the shared stamps and the shared templates nobody changed.</summary>
    private byte[] PackOwn() => LibraryArchive.Pack(_stampFolder, _templateFolder, file =>
    {
        if (IsReadOnly(file))
            return false;
        if (!_sharedTemplates.TryGetValue(Path.GetFullPath(file), out var shared))
            return true;
        try
        {
            return File.ReadAllText(file) != shared;
        }
        catch (IOException)
        {
            return false;
        }
    });

    public void Changed()
    {
        try
        {
            _ = Interop.SaveLibrary(Convert.ToBase64String(PackOwn()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the changes are there for this visit
        }
    }

    public async Task ExportAsync(TopLevel host)
    {
        var file = await host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("LibraryExport"),
            SuggestedFileName = $"{AppInfo.Name} {Loc.T("LibraryFileName")}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [ZipType()],
        });
        if (file == null)
            return;
        var zip = PackOwn();
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(zip);
    }

    public async Task<bool> ImportAsync(TopLevel host)
    {
        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("LibraryImport"),
            AllowMultiple = false,
            FileTypeFilter = [ZipType()],
        });
        if (files.Count == 0)
            return false;
        byte[] zip;
        await using (var stream = await files[0].OpenReadAsync())
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            zip = memory.ToArray();
        }
        var count = LibraryArchive.Extract(zip, LibraryArchive.Stamps, _stampFolder, ".png", subfolders: true)
            + LibraryArchive.Extract(zip, LibraryArchive.Templates, _templateFolder, TemplateLibrary.Extension, subfolders: false);
        if (count == 0)
            throw new InvalidDataException(Loc.T("LibraryImportEmpty"));
        Changed();
        return true;
    }

    private static FilePickerFileType ZipType() => new(Loc.T("LibraryZipFiles")) { Patterns = ["*.zip"], MimeTypes = ["application/zip"] };
}
