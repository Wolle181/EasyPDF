using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EasyPDF.Core.Settings;

namespace EasyPDF.Core.Templates;

/// <summary>
/// The user's templates folder: one text file per template, the file name (without .txt) is the template's name, the
/// content its text. A normal folder, so templates can also be written, copied and shared with any text editor.
/// </summary>
public static class TemplateLibrary
{
    public const string Extension = ".txt";

    /// <summary>Characters not allowed in file names on any of the supported systems.</summary>
    private static readonly char[] InvalidNameChars = [.. Path.GetInvalidFileNameChars().Union("<>:\"/\\|?*")];

    /// <summary>UTF-8 without byte order mark: what text editors on all systems read and write by default.</summary>
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Documents/&lt;app name&gt;/Templates.</summary>
    public static string DefaultFolder => AppInfo.DocumentsFolder("Templates");

    /// <summary>The configured templates folder, or <see cref="DefaultFolder"/> while none is set.</summary>
    public static string ResolveFolder(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.TemplateFolder) ? DefaultFolder : settings.TemplateFolder;

    /// <summary>Creates the folder if it is missing; false if that is not possible (e.g. no permission).</summary>
    public static bool TryEnsureFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the folder if needed and writes each sample into it whose id is not in <paramref name="provided"/> yet
    /// ("template:" + id), then adds it there: a sample the user deleted doesn't come back, one added by a later version
    /// still arrives, also in a folder that exists already. A folder that doesn't exist (any more) starts over with all
    /// samples, whatever the settings kept from earlier say. A file of the same name in the folder is kept. True if
    /// <paramref name="provided"/> changed; a failed write is tried again next time.
    /// </summary>
    public static bool ProvideSamples(string folder, IEnumerable<(string Id, TextTemplate Template)> samples, ICollection<string> provided)
    {
        var startOver = !Directory.Exists(folder);
        if (!TryEnsureFolder(folder))
            return false;
        var changed = false;
        foreach (var (id, sample) in samples)
        {
            var key = "template:" + id;
            var known = provided.Contains(key);
            if (known && !startOver)
                continue;
            try
            {
                var target = Path.Combine(folder, UniqueFileName(sample.Name, new HashSet<string>()));
                if (!File.Exists(target))
                    File.WriteAllText(target, ForFile(sample.Text), Utf8);
                if (!known)
                {
                    provided.Add(key);
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder stays usable without the sample
            }
        }
        return changed;
    }

    /// <summary>The templates of the folder sorted by name; unreadable files are skipped, a missing folder yields an empty list.</summary>
    public static IReadOnlyList<TextTemplate> Load(string folder)
    {
        var result = new List<TextTemplate>();
        try
        {
            if (!Directory.Exists(folder))
                return result;
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).Equals(Extension, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    // A final line break (most editors add one) is not part of the text
                    var text = File.ReadAllText(file).ReplaceLineEndings("\n").TrimEnd('\n');
                    result.Add(new TextTemplate { Name = Path.GetFileNameWithoutExtension(file), Text = text, FilePath = file });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return [.. result.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Writes the edited list into the folder: files of <paramref name="before"/> that are no longer used are deleted (also
    /// after a rename), each template of <paramref name="after"/> is written to a file named after it. Names that can't be
    /// file names are adjusted (invalid characters become "_", the same name twice gets " (2)"). False if a file could not
    /// be written or deleted; the others are saved anyway.
    /// </summary>
    public static bool Save(string folder, IEnumerable<TextTemplate> before, IReadOnlyList<TextTemplate> after)
    {
        if (!TryEnsureFolder(folder))
            return false;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = after.Select(t => Path.Combine(folder, UniqueFileName(t.Name, used))).ToList();
        var ok = true;
        // Delete first: on Windows and macOS a rename that only changes the case would otherwise delete the renamed file
        foreach (var old in before.Select(t => t.FilePath).OfType<string>())
        {
            if (targets.Contains(old, StringComparer.Ordinal))
                continue;
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        for (var i = 0; i < after.Count; i++)
        {
            try
            {
                File.WriteAllText(targets[i], ForFile(after[i].Text), Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>A file name for a template name: without characters files can't have, unique (case-insensitive) within <paramref name="used"/>.</summary>
    public static string UniqueFileName(string name, ISet<string> used, string extension = Extension)
    {
        var clean = new string([.. name.Select(c => char.IsControl(c) || InvalidNameChars.Contains(c) ? '_' : c)]).Trim().TrimEnd('.');
        if (clean.Length == 0)
            clean = "_";
        var candidate = clean;
        for (var n = 2; !used.Add(candidate + extension); n++)
            candidate = $"{clean} ({n})";
        return candidate + extension;
    }

    /// <summary>The text with the system's line breaks, so Notepad &amp; co. show it as typed.</summary>
    private static string ForFile(string text) => text.ReplaceLineEndings(Environment.NewLine);
}
