using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace EasyPDF.App.Controls.Ribbon;

/// <summary>Tabbed ribbon; double-clicking a tab (or Ctrl+F1 via <see cref="IsCollapsed"/>) collapses it to the tab row.</summary>
public class Ribbon : TabControl
{
    public static readonly StyledProperty<bool> IsCollapsedProperty =
        AvaloniaProperty.Register<Ribbon, bool>(nameof(IsCollapsed), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private ContentPresenter? _contentHost;

    static Ribbon()
    {
        IsCollapsedProperty.Changed.AddClassHandler<Ribbon>((r, _) => r.UpdateContentVisibility());
    }

    public Ribbon()
    {
        // Styled as TabControl (StyleKeyOverride), so type selectors can't target it; styles use `TabControl.ribbon`
        Classes.Add("ribbon");
        // A single click on any tab (also the selected one) opens a collapsed ribbon again. Tunnel, because the tab handles the press itself.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (IsCollapsed && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && e.Source is Visual v && v.FindAncestorOfType<TabItem>(includeSelf: true) != null)
            {
                IsCollapsed = false;
                _expandedByClickAt = DateTime.UtcNow;
            }
        }, RoutingStrategies.Tunnel);
        DoubleTapped += (_, e) =>
        {
            if (e.Source is not Visual v || v.FindAncestorOfType<TabItem>(includeSelf: true) == null)
                return;
            // The first click of this double-click already opened the ribbon: leave it open
            if (DateTime.UtcNow - _expandedByClickAt < DoubleClickWindow)
                return;
            IsCollapsed = !IsCollapsed;
        };
    }

    /// <summary>Longer than any system double-click time.</summary>
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(800);

    private DateTime _expandedByClickAt = DateTime.MinValue;

    protected override Type StyleKeyOverride => typeof(TabControl);

    public bool IsCollapsed
    {
        get => GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _contentHost = e.NameScope.Find<ContentPresenter>("PART_SelectedContentHost");
        UpdateContentVisibility();
    }

    private void UpdateContentVisibility()
    {
        if (_contentHost != null)
            _contentHost.IsVisible = !IsCollapsed;
    }
}
