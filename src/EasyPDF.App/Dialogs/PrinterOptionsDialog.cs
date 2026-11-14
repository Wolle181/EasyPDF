using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;

namespace EasyPDF.App.Dialogs;

/// <summary>
/// The CUPS options of a printer (tray, duplex, paper …), one drop-down each. The result holds only the options that differ
/// from the printer's defaults; null if cancelled (also when closed with [X]).
/// </summary>
public sealed class PrinterOptionsDialog : Window
{
    private readonly List<(PrinterOption Option, ComboBox Box)> _boxes = [];

    public PrinterOptionsDialog() : this("", [], null)
    {
    }

    public PrinterOptionsDialog(string printer, IReadOnlyList<PrinterOption> options, IReadOnlyDictionary<string, string>? current)
    {
        Title = Loc.F("PrinterOptionsTitle", printer);
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 8 };
        foreach (var option in options)
        {
            var box = new ComboBox { ItemsSource = option.Choices, HorizontalAlignment = HorizontalAlignment.Stretch };
            box.SelectedItem = current != null && current.TryGetValue(option.Key, out var chosen) && option.Choices.Contains(chosen) ? chosen : option.Default;
            var label = new TextBlock { Text = option.Label, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            Grid.SetRow(label, _boxes.Count);
            Grid.SetRow(box, _boxes.Count);
            Grid.SetColumn(box, 1);
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(label);
            grid.Children.Add(box);
            _boxes.Add((option, box));
        }

        var ok = new Button { Content = Loc.T("Ok"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Content = Loc.T("Cancel"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        var reset = new Button { Content = Loc.T("PrinterOptionsReset"), IsEnabled = options.Count > 0 };
        ok.Click += (_, _) => Close(Result());
        cancel.Click += (_, _) => Close((IReadOnlyDictionary<string, string>?)null);
        reset.Click += (_, _) =>
        {
            foreach (var (option, box) in _boxes)
                box.SelectedItem = option.Default;
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                options.Count == 0
                    ? new TextBlock { Text = Loc.T("PrinterOptionsNone"), TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                    : new ScrollViewer { MaxHeight = 460, Content = grid, Padding = new Thickness(0, 0, 16, 0) },
                new DockPanel
                {
                    Children =
                    {
                        reset,
                        new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
                    },
                },
            },
        };
    }

    /// <summary>The chosen values that differ from the printer's defaults (passed to <c>lp</c> as <c>-o key=value</c>).</summary>
    private IReadOnlyDictionary<string, string> Result() =>
        _boxes.Where(b => b.Box.SelectedItem is string value && value != b.Option.Default)
              .ToDictionary(b => b.Option.Key, b => (string)b.Box.SelectedItem!);
}
