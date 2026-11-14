using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WixToolset.Dtf.WindowsInstaller;

namespace EasyPDF.UninstallActions;

/// <summary>
/// An uninstall also removes the user's folders, after asking: settings (%AppData%\&lt;name&gt;), stamps and templates
/// (Documents\&lt;name&gt;, or %LocalAppData%\&lt;name&gt; where Documents could not be written, AppInfo.ChooseUserFolder). A folder
/// chosen in the settings stays: it can hold anything. Updates do not run these actions (condition in Package.wxs).
/// </summary>
public static class UserDataActions
{
    private const string RemoveProperty = "REMOVEUSERDATA";

    private static List<string> ExistingFolders(string name) =>
        new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData }
            .Select(f => Environment.GetFolderPath(f))
            .Where(f => f.Length > 0)
            .Select(f => Path.Combine(f, name))
            .Where(Directory.Exists)
            .ToList();

    /// <summary>The installer's language choice, "system" = the language of Windows.</summary>
    private static bool IsGerman(Session session)
    {
        var language = session["PREVLANGUAGE"];
        return language == "de" || (language != "en" && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de");
    }

    /// <summary>Before anything changes: OK goes on and removes the folders at the end, Cancel ends the uninstall with nothing changed.</summary>
    [CustomAction]
    public static ActionResult ConfirmRemoveUserData(Session session)
    {
        var name = session["ProductName"];
        var folders = ExistingFolders(name);
        if (folders.Count == 0)
            return ActionResult.Success;

        var list = string.Join("\n", folders);
        var text = IsGerman(session)
            ? $"Mit {name} werden auch diese Ordner und alle Dateien darin gelöscht:\n\n{list}\n\nDarin liegen Ihre Einstellungen, Stempel und Textvorlagen. Sichern Sie vorher, was Sie behalten möchten.\n\nOK: Deinstallieren und Ordner löschen.\nAbbrechen: Nichts wird geändert."
            : $"These folders and all files in them will be deleted together with {name}:\n\n{list}\n\nThey hold your settings, stamps and text templates. Back up what you want to keep first.\n\nOK: Uninstall and delete the folders.\nCancel: Nothing is changed.";
        // The text goes in as a field: as the format string, [ and \ in a path would be read as formatting
        using var record = new Record(1) { FormatString = "[1]" };
        record[1] = text;
        var kind = InstallMessage.User | (InstallMessage)MessageButtons.OKCancel | (InstallMessage)MessageIcon.Warning | (InstallMessage)MessageDefaultButton.Button2;
        try
        {
            if (session.Message(kind, record) == MessageResult.OK)
                session[RemoveProperty] = "1";
            else // no way to ask (e.g. msiexec /passive): the program goes, the user's files stay
                session.Log("User data: could not ask, the folders stay.");
            return ActionResult.Success;
        }
        catch (InstallCanceledException) // how Session.Message reports Cancel
        {
            session.Log("User data: the user cancelled the uninstall.");
            return ActionResult.UserExit;
        }
    }

    /// <summary>After the program is gone, so a failed uninstall leaves the folders alone. What can't be deleted stays; the uninstall has succeeded anyway.</summary>
    [CustomAction]
    public static ActionResult RemoveUserData(Session session)
    {
        foreach (var folder in ExistingFolders(session["ProductName"]))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, System.IO.FileAttributes.Normal); // Directory.Delete refuses read-only files
                Directory.Delete(folder, recursive: true);
                session.Log("User data: removed " + folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                session.Log("User data: could not remove " + folder + ": " + ex.Message);
            }
        }
        return ActionResult.Success;
    }
}
