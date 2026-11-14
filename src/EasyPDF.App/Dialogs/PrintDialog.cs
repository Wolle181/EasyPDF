using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.Dialogs;

/// <summary>
/// Printer (with its driver settings via "Properties…"), copies and page range; the result is null if cancelled (also when closed with [X]).
/// </summary>
public sealed class PrintDialog : Window
{
    private readonly PrintDialogContext _context;
    private readonly Dictionary<string, PrinterSetup> _setups;
    private readonly ComboBox _printer = new() { Width = 260 };
    private readonly Button _properties = new();
    private readonly TextBlock _setupHint = new() { FontSize = 12, Margin = new Thickness(88, -6, 0, 0), IsVisible = false };
    private readonly NumericUpDown _copies = new() { Minimum = 1, Maximum = 99, Value = 1, Width = 120, FormatString = "0" };
    private readonly RadioButton _current = new() { GroupName = "Scope" };
    private readonly RadioButton _range = new() { GroupName = "Scope" };
    private readonly RadioButton _all = new() { GroupName = "Scope" };
    private readonly TextBox _rangeText = new() { PlaceholderText = "1-3, 5, 8-10", Margin = new Thickness(28, 0, 0, 0) };
    private readonly TextBlock _error = new() { Foreground = Avalonia.Media.Brushes.Red, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _ok = new() { MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };

    public PrintDialog() : this(new PrintDialogContext(1, 0, [], null))
    {
    }

    public PrintDialog(PrintDialogContext context)
    {
        _context = context;
        _setups = context.Setups?.ToDictionary() ?? [];
        Title = Loc.T("PrintTitle");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;

        _printer.ItemsSource = context.Printers;
        _printer.SelectedItem = context.DefaultPrinter != null && context.Printers.Contains(context.DefaultPrinter) ? context.DefaultPrinter : context.Printers.FirstOrDefault();
        _current.Content = Loc.F("PrintScopeCurrent", context.CurrentPage + 1);
        _range.Content = Loc.T("PrintScopeRange");
        _all.Content = Loc.F("PrintScopeAll", context.PageCount);
        var selected = context.SelectedPages ?? [];
        _rangeText.Text = selected.Count > 1 ? PageRangeParser.Format(selected) : $"{context.CurrentPage + 1}-{context.PageCount}";
        (selected.Count switch { 0 => _all, 1 => _current, _ => _range }).IsChecked = true;
        _ok.Content = Loc.T("PrintOk");
        _properties.Content = Loc.T("PrintProperties");
        _properties.IsVisible = context.Printing != null;
        _properties.Click += async (_, _) => await EditPropertiesAsync();
        _setupHint.Text = Loc.T("PrintCustomSetup");
        var cancel = new Button { Content = Loc.T("Cancel"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        cancel.Click += (_, _) => Close((PrintRequest?)null);
        _ok.Click += (_, _) => Close(BuildResult());

        void Changed(object? s, EventArgs e) => Validate();
        _range.IsCheckedChanged += Changed;
        _current.IsCheckedChanged += Changed;
        _all.IsCheckedChanged += Changed;
        // The box also reports its first text when it is shown: only a real change chooses the range
        var rangeShown = _rangeText.Text;
        _rangeText.TextChanged += (_, _) =>
        {
            if (_rangeText.Text == rangeShown)
                return;
            rangeShown = _rangeText.Text;
            _range.IsChecked = true;
            Validate();
        };
        _printer.SelectionChanged += (_, _) => Validate();

        static Control Row(string key, Control editor) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = Loc.T(key), Width = 80, VerticalAlignment = VerticalAlignment.Center }, editor },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                Row("PrintPrinter", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _printer, _properties } }),
                _setupHint,
                Row("PrintCopies", _copies),
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = Loc.T("PrintPages"), FontWeight = Avalonia.Media.FontWeight.SemiBold },
                        _current,
                        _range,
                        _rangeText,
                        _all,
                    },
                },
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { _ok, cancel } },
            },
        };
        Validate();
    }

    private PrintScope Scope => _range.IsChecked == true ? PrintScope.Range : _all.IsChecked == true ? PrintScope.AllPages : PrintScope.CurrentPage;

    private System.Collections.Generic.IReadOnlyList<int>? Pages =>
        PrintRange.Resolve(Scope, _context.PageCount, _context.CurrentPage, _rangeText.Text);

    private PrinterSetup? Setup => _printer.SelectedItem is string printer && _setups.TryGetValue(printer, out var setup) ? setup : null;

    /// <summary>The printer's own settings (Windows: driver dialog); they stay with this printer, also for the next print job.</summary>
    private async System.Threading.Tasks.Task EditPropertiesAsync()
    {
        if (_context.Printing == null || _printer.SelectedItem is not string printer)
            return;
        _properties.IsEnabled = false;
        try
        {
            if (await _context.Printing.EditSetupAsync(this, printer, Setup) is { } setup)
                _setups[printer] = setup;
            Validate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _error.Text = Loc.F("PrintPropertiesFailed", ex.Message);
        }
        finally
        {
            _properties.IsEnabled = _printer.SelectedItem != null;
        }
    }

    private void Validate()
    {
        _properties.IsEnabled = _printer.SelectedItem != null;
        _setupHint.IsVisible = Setup is { } s && (s.DevMode != null || s.Options.Count > 0);
        var problem = _context.Printers.Count == 0 ? Loc.T("PrintNoPrinters")
            : _printer.SelectedItem == null ? Loc.T("PrintNoPrinters")
            : Pages == null ? RangeErrors.Text(PageRangeParser.Parse(_rangeText.Text, _context.PageCount))
            : "";
        _error.Text = problem;
        _ok.IsEnabled = problem.Length == 0;
    }

    private PrintRequest? BuildResult() =>
        _printer.SelectedItem is string printer && Pages is { } pages ? new PrintRequest(printer, (int)(_copies.Value ?? 1), pages, Setup) : null;
}
