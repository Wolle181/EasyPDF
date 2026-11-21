using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Downloads;

namespace EasyPDF.App.Dialogs;

/// <summary>
/// Web version: the desktop packages for download, one row per kind (what it is, file name and size, Download); those for
/// the browser's system are marked. Closing it (also with [X]) has no result.
/// </summary>
public sealed class DownloadsView : DialogView
{
    public DownloadsView(DesktopDownloads.WebPackages packages)
    {
        Title = Loc.T("DownloadsTitle");
        DialogWidth = 560;
        var rows = new StackPanel { Spacing = 10 };
        foreach (var package in packages.Packages)
            rows.Children.Add(Row(package, packages.ThisSystem.Contains(package.Kind), packages));
        var close = new Button
        {
            Content = Loc.T("Close"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true,
        };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = Loc.T("DownloadsMessage"), TextWrapping = TextWrapping.Wrap },
                rows,
                close,
            },
        };
    }

    /// <summary>The name of the row's Download button (tests find it by it).</summary>
    public static string ButtonName(DesktopPackageKind kind) => "Download" + kind;

    /// <summary>Resource keys of what a package is and a hint how to install it.</summary>
    private static (string Title, string Hint) Texts(DesktopPackageKind kind) => kind switch
    {
        DesktopPackageKind.WindowsInstaller => ("DownloadKindWindowsInstaller", "DownloadHintWindowsInstaller"),
        DesktopPackageKind.WindowsPortable => ("DownloadKindWindowsPortable", "DownloadHintWindowsPortable"),
        DesktopPackageKind.MacOS => ("DownloadKindMacOS", "DownloadHintMacOS"),
        DesktopPackageKind.LinuxDeb => ("DownloadKindLinuxDeb", "DownloadHintLinuxDeb"),
        _ => ("DownloadKindLinuxTar", "DownloadHintLinuxTar"),
    };

    private static Control Row(DesktopPackage package, bool forThisSystem, DesktopDownloads.WebPackages packages)
    {
        var download = new Button
        {
            Name = ButtonName(package.Kind),
            Content = Loc.T("DownloadsDownload"),
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (forThisSystem)
            download.Classes.Add("primary");
        download.Click += (_, _) => packages.Download(package);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = Loc.T(Texts(package.Kind).Title), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (forThisSystem)
        {
            var label = new TextBlock { Text = Loc.T("DownloadsThisSystem"), FontSize = 12 };
            var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Child = label };
            pill.Bind(Border.BackgroundProperty, pill.GetResourceObservable("Pill.Background"));
            label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("Pill.Foreground"));
            title.Children.Add(pill);
        }
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = Loc.T(Texts(package.Kind).Hint), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        text.Children.Add(new TextBlock
        {
            Text = Loc.F("DownloadsFile", package.Name, AttachmentsDialogView.FormatSize(package.Size)),
            FontSize = 12, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var row = new DockPanel();
        DockPanel.SetDock(download, Dock.Right);
        download.Margin = new Thickness(12, 0, 0, 0);
        row.Children.Add(download);
        row.Children.Add(text);
        return row;
    }
}
