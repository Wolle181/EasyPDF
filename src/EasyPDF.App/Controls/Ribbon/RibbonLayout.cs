using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using EasyPDF.App.Localization;

namespace EasyPDF.App.Controls.Ribbon;

/// <summary>
/// Titled group of ribbon controls with a caption below. In a narrow window the ribbon reduces it step by step
/// (<see cref="Reduction"/>), like Office: 0 = as designed, 1 = large buttons shown small, 2 = icons only,
/// 3 = collapsed to one button that opens the whole group (at full size) below it.
/// </summary>
public class RibbonGroup : ContentControl
{
    public const int Collapsed = 3;

    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<RibbonGroup, string?>(nameof(Caption));

    public static readonly StyledProperty<int> ReductionProperty =
        AvaloniaProperty.Register<RibbonGroup, int>(nameof(Reduction));

    private Control? _ownContent;      // the group's controls while they sit in the drop-down of the collapsed group
    private RibbonButton? _collapsedButton;

    static RibbonGroup()
    {
        ReductionProperty.Changed.AddClassHandler<RibbonGroup>((g, e) => g.ApplyReduction(e.GetNewValue<int>()));
    }

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public int Reduction
    {
        get => GetValue(ReductionProperty);
        set => SetValue(ReductionProperty, value);
    }

    /// <summary>The button standing for the collapsed group; null while the group shows its controls.</summary>
    public RibbonButton? CollapsedButton => _collapsedButton;

    private string CollapsedLabel => $"{Caption} ▾";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CaptionProperty && _collapsedButton != null)
            _collapsedButton.Label = CollapsedLabel; // language change
    }

    protected override Type StyleKeyOverride => typeof(RibbonGroup);

    /// <summary>The group's own buttons, also while they are in the drop-down.</summary>
    public IEnumerable<RibbonButton> Buttons()
    {
        var root = _ownContent ?? Content as Control;
        if (root == null)
            return [];
        return root is RibbonButton self ? [self] : root.GetLogicalDescendants().OfType<RibbonButton>();
    }

    private void ApplyReduction(int level)
    {
        if (level >= Collapsed && _collapsedButton == null)
            Collapse();
        else if (level < Collapsed && _collapsedButton != null)
            Expand();
        // In the drop-down the controls are shown as designed
        foreach (var button in Buttons())
            button.Reduction = level >= Collapsed ? 0 : level;
        InvalidateWholeMeasure();
    }

    /// <summary>
    /// A child that changes only invalidates itself; the ribbon measures the group right away, so the whole group has
    /// to be measured again (otherwise it would report its previous size).
    /// </summary>
    internal void InvalidateWholeMeasure()
    {
        InvalidateMeasure();
        foreach (var layoutable in this.GetVisualDescendants().OfType<Layoutable>())
            layoutable.InvalidateMeasure();
    }

    private void Collapse()
    {
        if (Content is not Control content)
            return;
        var first = Buttons().FirstOrDefault(b => b.Icon != null);
        Content = null;
        _ownContent = content;
        // Keep the bindings of the controls working while they are not in the window (commands, enabled state)
        content.Bind(DataContextProperty, this.GetObservable(DataContextProperty));
        var flyout = new Flyout { Content = content, Placement = PlacementMode.BottomEdgeAlignedLeft };
        content.AddHandler(Button.ClickEvent, OnDropDownClick);

        var button = new RibbonButton { IsLarge = true, Icon = first?.Icon, IconAccent = first?.IconAccent, Flyout = flyout, Label = CollapsedLabel };
        if (first != null)
            button.Bind(RibbonButton.IconColorProperty, first.GetObservable(RibbonButton.IconColorProperty));
        _collapsedButton = button;
        Content = button;
    }

    private void Expand()
    {
        var content = _ownContent!;
        var flyout = (Flyout)_collapsedButton!.Flyout!;
        flyout.Hide();
        flyout.Content = null;
        content.RemoveHandler(Button.ClickEvent, OnDropDownClick);
        content.ClearValue(DataContextProperty);
        _ownContent = null;
        _collapsedButton = null;
        Content = content;
    }

    /// <summary>A command chosen in the drop-down closes it (a button with its own menu keeps it open for that menu).</summary>
    private void OnDropDownClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Flyout: null })
            _collapsedButton?.Flyout?.Hide();
    }
}

/// <summary>
/// Lays out the controls of a ribbon group in columns: a large button fills a column, small buttons and other controls
/// are stacked three to a column (vertically centred). When large buttons are shown small they flow into those columns.
/// </summary>
public class RibbonGroupPanel : Panel
{
    public const int Rows = 3;

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<RibbonGroupPanel, double>(nameof(ColumnSpacing));

    static RibbonGroupPanel()
    {
        AffectsMeasure<RibbonGroupPanel>(ColumnSpacingProperty);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    private sealed record Column(List<Control> Items, bool Large)
    {
        public double Width { get; set; }
    }

    private List<Column> BuildColumns()
    {
        var columns = new List<Column>();
        Column? small = null;
        foreach (var child in Children.Where(c => c.IsVisible))
        {
            if (child is RibbonButton { IsShownLarge: true })
            {
                columns.Add(new Column([child], Large: true));
                small = null;
                continue;
            }
            if (small == null || small.Items.Count == Rows)
                columns.Add(small = new Column([], Large: false));
            small.Items.Add(child);
        }
        return columns;
    }

    private List<Column> _columns = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        _columns = BuildColumns();
        double width = 0, height = 0;
        foreach (var column in _columns)
        {
            double columnHeight = 0;
            foreach (var item in column.Items)
            {
                item.Measure(new Size(double.PositiveInfinity, column.Large ? availableSize.Height : double.PositiveInfinity));
                column.Width = Math.Max(column.Width, item.DesiredSize.Width);
                columnHeight += item.DesiredSize.Height;
            }
            width += column.Width;
            height = Math.Max(height, columnHeight);
        }
        width += ColumnSpacing * Math.Max(0, _columns.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var column in _columns)
        {
            if (column.Large)
                column.Items[0].Arrange(new Rect(x, 0, column.Width, finalSize.Height));
            else
            {
                var y = Math.Max(0, (finalSize.Height - column.Items.Sum(i => i.DesiredSize.Height)) / 2);
                foreach (var item in column.Items)
                {
                    item.Arrange(new Rect(x, y, column.Width, item.DesiredSize.Height));
                    y += item.DesiredSize.Height;
                }
            }
            x += column.Width + ColumnSpacing;
        }
        return finalSize;
    }
}

/// <summary>
/// The groups of a ribbon tab side by side. If they do not fit, they are reduced like in Office, starting at the right:
/// large buttons become small, then icons only, then groups collapse to one button; afterwards the groups on the left
/// get back whatever room is left, so they keep their labels as long as possible.
/// </summary>
public class RibbonGroupsPanel : Panel
{
    // Width of each group at each reduction level, measured once (cleared when a group's size changes, e.g. new language)
    private readonly Dictionary<(RibbonGroup Group, int Level), double> _widths = [];
    private double _decidedFor = double.NaN;

    private List<RibbonGroup> Groups => [.. Children.OfType<RibbonGroup>()];

    // A new language changes the labels; the groups would still report their old sizes and the widths of the other
    // levels are outdated: measure everything again
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.Instance.PropertyChanged += OnLanguageChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Loc.Instance.PropertyChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        _widths.Clear();
        _decidedFor = double.NaN;
        foreach (var group in Groups)
            group.InvalidateWholeMeasure();
        InvalidateMeasure();
    }

    private double WidthOf(RibbonGroup group, int level, double height)
    {
        if (_widths.TryGetValue((group, level), out var width))
            return width;
        group.Reduction = level;
        group.Measure(new Size(double.PositiveInfinity, height));
        return _widths[(group, level)] = group.DesiredSize.Width;
    }

    private void Decide(List<RibbonGroup> groups, Size available)
    {
        var height = available.Height;
        var levels = new int[groups.Count];
        double Width(int i, int level) => WidthOf(groups[i], level, height);
        bool Fits() => levels.Select((level, i) => Width(i, level)).Sum() <= available.Width;

        if (!Fits())
        {
            // From the right: large buttons small, then icons only, then collapsed; a level is only taken if it makes
            // the group narrower (a group of two small buttons is narrower as icons than as a collapsed button)
            for (var level = 1; level <= RibbonGroup.Collapsed && !Fits(); level++)
                for (var i = groups.Count - 1; i >= 0 && !Fits(); i--)
                    if (Width(i, level) < Width(i, levels[i]))
                        levels[i] = level;
            // The last step may have freed more than needed: give it back from the left, but never so that a group is
            // shown larger than one to its left (the right groups stay the first to shrink)
            for (var i = 0; i < groups.Count; i++)
                for (var level = i == 0 ? 0 : levels.Take(i).Max(); level < levels[i]; level++)
                {
                    var current = levels[i];
                    levels[i] = level;
                    if (Fits())
                        break;
                    levels[i] = current;
                }
        }
        for (var i = 0; i < groups.Count; i++)
            groups[i].Reduction = levels[i];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var groups = Groups;
        if (!double.IsInfinity(availableSize.Width))
        {
            // A group whose size changed (texts, enabled commands do not count) makes all measured widths doubtful
            var stale = false;
            foreach (var g in groups) // every group is measured (no short cut: their sizes are stored below)
            {
                g.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                stale |= !_widths.TryGetValue((g, g.Reduction), out var w) || Math.Abs(w - g.DesiredSize.Width) > 0.5;
            }
            if (stale)
            {
                _widths.Clear();
                foreach (var g in groups)
                    _widths[(g, g.Reduction)] = g.DesiredSize.Width;
            }
            if (stale || availableSize.Width != _decidedFor)
            {
                _decidedFor = availableSize.Width;
                Decide(groups, availableSize);
            }
        }

        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }
        return finalSize;
    }
}
