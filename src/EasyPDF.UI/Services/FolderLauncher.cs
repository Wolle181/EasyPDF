using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;

namespace EasyPDF.App.Services;

/// <summary>Opens the stamp or templates folder in the file manager (created first if it does not exist yet); tells the user if that fails.</summary>
public static class FolderLauncher
{
    public static async Task OpenAsync(Window? owner, string folder)
    {
        if (owner == null)
            return;
        var problem = await TryOpenAsync(owner, folder);
        if (problem != null)
            await new MessageDialog(Loc.F("FolderOpenFailed", folder), problem + Environment.NewLine + Environment.NewLine + Loc.T("FolderProblemHint")).ShowDialog(owner);
    }

    /// <summary>Null if the folder was opened, otherwise the reason.</summary>
    private static async Task<string?> TryOpenAsync(TopLevel owner, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return await owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)) ? null : Loc.T("FolderNoFileManager");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ex.Message;
        }
    }
}
