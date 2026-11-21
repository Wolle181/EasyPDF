using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EasyPDF.App.Localization;
using EasyPDF.Core.Templates;

namespace EasyPDF.App.Dialogs;

/// <summary>Edits the text templates; the result is the new list, or null if cancelled (also when closed with [X]).</summary>
public sealed class TemplatesView : DialogView
{
    private readonly List<TextTemplate> _items;
    private readonly ListBox _list = new() { MinHeight = 200, Width = 180 };
    private readonly TextBox _name = new();
    private readonly TextBox _text = new() { AcceptsReturn = true, MinHeight = 120, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
    private bool _loading;
    private bool _refreshing;

    public TemplatesView() : this([])
    {
    }

    public TemplatesView(IEnumerable<TextTemplate> templates)
    {
        _items = [.. templates.Select(t => new TextTemplate { Name = t.Name, Text = t.Text, FilePath = t.FilePath })];
        Title = Loc.T("TemplatesTitle");
        DialogWidth = 620;

        var add = new Button { Content = Loc.T("TemplateAdd") };
        var delete = new Button { Content = Loc.T("TemplateDelete") };
        var ok = new Button { Content = Loc.T("Ok"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Content = Loc.T("Cancel"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        add.Click += (_, _) =>
        {
            _items.Add(new TextTemplate { Name = Loc.T("TemplateAdd"), Text = "" });
            Refresh(_items.Count - 1);
            _name.Focus();
            _name.SelectAll();
        };
        delete.Click += (_, _) =>
        {
            var index = _list.SelectedIndex;
            if (index < 0)
                return;
            _items.RemoveAt(index);
            Refresh(System.Math.Min(index, _items.Count - 1));
        };
        ok.Click += (_, _) => Close(_items.Where(t => t.Name.Trim().Length > 0).ToList());
        cancel.Click += (_, _) => Close((List<TextTemplate>?)null);

        _list.SelectionChanged += (_, _) =>
        {
            if (!_refreshing)
                ShowSelected();
        };
        _name.TextChanged += (_, _) =>
        {
            if (_loading || Selected is not { } item)
                return;
            item.Name = _name.Text ?? ""; // the list shows the new name once the box is left
        };
        _name.LostFocus += (_, _) =>
        {
            if (_list.SelectedIndex >= 0)
                Refresh(_list.SelectedIndex);
        };
        _text.TextChanged += (_, _) =>
        {
            if (!_loading && Selected is { } item)
                item.Text = _text.Text ?? "";
        };

        var editor = new StackPanel
        {
            Spacing = 6,
            Width = 380,
            Children =
            {
                new TextBlock { Text = Loc.T("TemplateName") }, _name,
                new TextBlock { Text = Loc.T("TemplateText") }, _text,
                new TextBlock { Text = Loc.T("TemplateMacros"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 },
            },
        };
        var left = new StackPanel
        {
            Spacing = 6,
            Children = { _list, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, delete } } },
        };
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { left, editor } },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
            },
        };
        Refresh(_items.Count > 0 ? 0 : -1);
    }

    private TextTemplate? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _items.Count ? _items[_list.SelectedIndex] : null;

    /// <summary>
    /// Shows the list anew and selects an entry. The boxes only change once at the end: while the list is rebuilt nothing
    /// is selected, and disabling the boxes for that moment would take the focus from the one the user just clicked into.
    /// </summary>
    private void Refresh(int select)
    {
        _refreshing = true;
        _list.ItemsSource = _items.Select(t => t.Name).ToList();
        _list.SelectedIndex = select;
        _refreshing = false;
        ShowSelected();
    }

    private void ShowSelected()
    {
        _loading = true;
        var item = Selected;
        _name.Text = item?.Name ?? "";
        _text.Text = item?.Text ?? "";
        _name.IsEnabled = _text.IsEnabled = item != null;
        _loading = false;
    }
}

/// <summary>The templates dialog as a window (desktop).</summary>
public sealed class TemplatesDialog : DialogWindow
{
    public TemplatesDialog(IEnumerable<TextTemplate> templates) : base(new TemplatesView(templates))
    {
    }
}
