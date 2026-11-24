using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using EasyPDF.App.Localization;
using EasyPDF.App.Views;
using EasyPDF.Remote;

namespace EasyPDF.Browser;

/// <summary>Tells the user why the server did not take a file (in their language).</summary>
internal static class UploadErrors
{
    public static string Message(string code) => code switch
    {
        ApiErrors.NotPdf => Loc.T("WebErrNotPdf"),
        ApiErrors.TooLarge => Loc.T("WebErrTooLarge"),
        ApiErrors.QuotaExceeded => Loc.T("WebErrQuota"),
        ApiErrors.PasswordProtected => Loc.T("WebErrPassword"),
        ApiErrors.Damaged => Loc.T("WebErrDamaged"),
        ApiErrors.TooManyRequests => Loc.T("WebErrTooManyRequests"),
        ApiErrors.NoConnection => Loc.T("WebErrNoConnection"),
        _ => Loc.T("WebErrServer"),
    };

    public static Task ShowAsync(RemoteApiException error, string fileName)
    {
        if ((Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView is MainView view)
            return view.ShowErrorAsync(Message(error.Code), fileName);
        return Task.CompletedTask;
    }
}
