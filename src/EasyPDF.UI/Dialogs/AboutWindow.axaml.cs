using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;

namespace EasyPDF.App.Dialogs;

public sealed record AboutRow(string Label, string Value);

public partial class AboutView : DialogView
{
    public AboutView()
    {
        InitializeComponent();
        // The web version has no license folder next to it (the texts are in the repository)
        LicensesButton.IsVisible = !PlatformFeatures.Current.IsWeb;
        InfoList.ItemsSource = VersionInfo.Collect().Select(i => new AboutRow(Loc.T(i.LabelKey), i.Value)).ToList();
        ShowCompanyLogo(EasyPDF.App.Styles.BrandLogo.Company);
    }

    /// <summary>The brand's company logo next to the program name; hidden without one.</summary>
    public void ShowCompanyLogo(Avalonia.Media.IImage? logo)
    {
        CompanyLogo.Source = logo;
        CompanyLogoFrame.IsVisible = logo != null;
        ToolTip.SetTip(CompanyLogoFrame, VersionInfo.Company);
        Avalonia.Automation.AutomationProperties.SetName(CompanyLogo, VersionInfo.Company);
    }

    /// <summary>All details as plain text, for bug reports.</summary>
    public static string BuildText() =>
        Loc.T("AppName") + "\n" + string.Join("\n", VersionInfo.Collect().Select(i => $"{Loc.T(i.LabelKey)}: {i.Value}"));

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Host?.Clipboard is { } clipboard)
            await clipboard.SetValueAsync(Avalonia.Input.DataFormat.Text, BuildText());
    }

    /// <summary>Opens the folder with the license texts in the file manager.</summary>
    private async void OnLicenses(object? sender, RoutedEventArgs e)
    {
        if (System.IO.Directory.Exists(VersionInfo.LicenseFolder) && Host is { } host)
            await host.Launcher.LaunchDirectoryInfoAsync(new System.IO.DirectoryInfo(VersionInfo.LicenseFolder));
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>Help &gt; About as a window (desktop).</summary>
public sealed class AboutWindow : DialogWindow
{
    public AboutWindow() : base(new AboutView())
    {
    }

    public void ShowCompanyLogo(Avalonia.Media.IImage? logo) => ((AboutView)DialogContent).ShowCompanyLogo(logo);

    public static string BuildText() => AboutView.BuildText();
}
