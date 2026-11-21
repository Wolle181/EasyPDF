using System.Collections.Generic;
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
/// Web version: the desktop packages for download. A choice of the system (Windows, macOS, Linux; preselected: the
/// browser's, systems without packages disabled) shows its packages, one row per kind (what it is, file name and size,
/// Download); the browser's system is marked. Closing it (also with [X]) has no result.
/// </summary>
public sealed class DownloadsView : DialogView
{
    private readonly DesktopDownloads.WebPackages _packages;
    private readonly StackPanel _rows = new() { Spacing = 10 };

    public DownloadsView(DesktopDownloads.WebPackages packages)
    {
        _packages = packages;
        Title = Loc.T("DownloadsTitle");
        DialogWidth = 560;
        var available = packages.Packages.Select(p => SystemOf(p.Kind)).ToHashSet();
        var own = packages.ThisSystem.Select(SystemOf).Where(available.Contains).Cast<DesktopSystem?>().FirstOrDefault();
        var selected = own ?? Systems.FirstOrDefault(available.Contains);
        var choice = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        choice.Children.Add(new TextBlock { Text = Loc.T("DownloadsSystem"), VerticalAlignment = VerticalAlignment.Center });
        foreach (var system in Systems)
        {
            var radio = new RadioButton
            {
                Name = SystemButtonName(system),
                GroupName = "DownloadSystem",
                Content = own == system ? Loc.F("DownloadsSystemOwn", SystemName(system)) : SystemName(system),
                IsEnabled = available.Contains(system),
                IsChecked = system == selected,
            };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                    ShowSystem(system);
            };
            choice.Children.Add(radio);
        }
        ShowSystem(selected);
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
                choice,
                _rows,
                close,
            },
        };
    }

    /// <summary>The systems in the order of the choice.</summary>
    public static readonly DesktopSystem[] Systems = [DesktopSystem.Windows, DesktopSystem.MacOS, DesktopSystem.Linux];

    public static DesktopSystem SystemOf(DesktopPackageKind kind) => kind switch
    {
        DesktopPackageKind.WindowsInstaller or DesktopPackageKind.WindowsPortable => DesktopSystem.Windows,
        DesktopPackageKind.MacOS => DesktopSystem.MacOS,
        _ => DesktopSystem.Linux,
    };

    /// <summary>The name of a system's choice (tests find it by it).</summary>
    public static string SystemButtonName(DesktopSystem system) => "DownloadSystem" + system;

    private static string SystemName(DesktopSystem system) => Loc.T(system switch
    {
        DesktopSystem.Windows => "DownloadsSystemWindows",
        DesktopSystem.MacOS => "DownloadsSystemMacOS",
        _ => "DownloadsSystemLinux",
    });

    private void ShowSystem(DesktopSystem system)
    {
        _rows.Children.Clear();
        foreach (var package in _packages.Packages.Where(p => SystemOf(p.Kind) == system))
            _rows.Children.Add(Row(package, _packages));
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

    private static Control Row(DesktopPackage package, DesktopDownloads.WebPackages packages)
    {
        var download = new Button
        {
            Name = ButtonName(package.Kind),
            Content = Loc.T("DownloadsDownload"),
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        download.Classes.Add("primary");
        download.Click += (_, _) => packages.Download(package);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = Loc.T(Texts(package.Kind).Title), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
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

/// <summary>The systems the downloads dialog lets the user choose.</summary>
public enum DesktopSystem
{
    Windows,
    MacOS,
    Linux,
}
