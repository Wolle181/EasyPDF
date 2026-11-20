using System.Linq;
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace EasyPDF.App.Controls.Ribbon;

/// <summary>Implemented by the window: tells ribbon buttons which shortcut triggers their command.</summary>
public interface IShortcutProvider
{
    string? ShortcutFor(System.Windows.Input.ICommand? command);

    /// <summary>False for commands the platform does not offer (web version): their buttons are hidden.</summary>
    bool IsAvailable(System.Windows.Input.ICommand? command) => true;
}

/// <summary>
/// Ribbon command button: large (icon above text) or small (icon beside text). Looks like a normal Fluent button.
/// </summary>
public class RibbonButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<RibbonButton, Geometry?>(nameof(Icon));

    /// <summary>Second layer of the icon, drawn in the accent colour.</summary>
    public static readonly StyledProperty<Geometry?> IconAccentProperty =
        AvaloniaProperty.Register<RibbonButton, Geometry?>(nameof(IconAccent));

    /// <summary>Colour of the action layer of the icon; also switches on the soft fill of the body (Icon.Tint). The outline stays in the ink colour.</summary>
    public static readonly StyledProperty<IBrush?> IconColorProperty =
        AvaloniaProperty.Register<RibbonButton, IBrush?>(nameof(IconColor));

    /// <summary>Icon size in pixels; 0 = automatic (large 32, small 18).</summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<RibbonButton, double>(nameof(IconSize));

    /// <summary>
    /// Diameter of the round frame for a button with a fixed <see cref="IconSize"/> (e.g. the quick guide); 0 = a fixed icon
    /// size is drawn bare. Buttons with automatic icon size always get their frame (large 38, small 22).
    /// </summary>
    public static readonly StyledProperty<double> CircleSizeProperty =
        AvaloniaProperty.Register<RibbonButton, double>(nameof(CircleSize));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<RibbonButton, string?>(nameof(Label));

    public static readonly StyledProperty<bool> IsLargeProperty =
        AvaloniaProperty.Register<RibbonButton, bool>(nameof(IsLarge));

    /// <summary>Highlights the button as the active choice (e.g. the current mode).</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<RibbonButton, bool>(nameof(IsActive));

    /// <summary>Keyboard shortcut text (e.g. "Ctrl+O"); shown in the tooltip after the label.</summary>
    public static readonly StyledProperty<string?> ShortcutProperty =
        AvaloniaProperty.Register<RibbonButton, string?>(nameof(Shortcut));

    /// <summary>
    /// Set by a narrow ribbon (<see cref="RibbonGroup.Reduction"/>): 0 = as designed, 1 = large buttons are shown small,
    /// 2 = icon only (the label stays in the tooltip; buttons without icon keep their label).
    /// </summary>
    public static readonly StyledProperty<int> ReductionProperty =
        AvaloniaProperty.Register<RibbonButton, int>(nameof(Reduction));

    static RibbonButton()
    {
        ReductionProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.Rebuild());
        ShortcutProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.UpdateToolTip());
        IsActiveProperty.Changed.AddClassHandler<RibbonButton>((b, e) =>
        {
            b.Classes.Set("active", e.GetNewValue<bool>());
            b.Rebuild(); // the action layer turns white on the filled circle
        });
        IconProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.Rebuild());
        IconAccentProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.Rebuild());
        IconSizeProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.Rebuild());
        CircleSizeProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.Rebuild());
        IconColorProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.Rebuild());
        LabelProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.Rebuild());
        IsLargeProperty.Changed.AddClassHandler<RibbonButton>((b, _) => b.Rebuild());
    }

    public RibbonButton()
    {
        Classes.Add("ribbon");
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Rebuild();
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public Geometry? IconAccent
    {
        get => GetValue(IconAccentProperty);
        set => SetValue(IconAccentProperty, value);
    }

    public IBrush? IconColor
    {
        get => GetValue(IconColorProperty);
        set => SetValue(IconColorProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double CircleSize
    {
        get => GetValue(CircleSizeProperty);
        set => SetValue(CircleSizeProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var provider = this.GetVisualAncestors().OfType<IShortcutProvider>().FirstOrDefault();
        // Decorative buttons (the icons of the quick guide) take no input and are not commands to hide
        if (provider != null && IsHitTestVisible && !provider.IsAvailable(Command))
        {
            IsVisible = false;
            // A group whose buttons are all unavailable goes as a whole (not just its caption left over)
            if (this.FindAncestorOfType<RibbonGroup>() is { } group
                && group.GetLogicalDescendants().OfType<RibbonButton>().All(b => !provider.IsAvailable(b.Command)))
                group.IsVisible = false;
        }
        if (Shortcut == null && provider != null)
            Shortcut = provider.ShortcutFor(Command);
    }

    public string? Shortcut
    {
        get => GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    /// <summary>The tooltip repeats the label (small buttons may be cut off) and adds the shortcut. Buttons without label keep their own tip.</summary>
    private void UpdateToolTip()
    {
        if (string.IsNullOrEmpty(Label))
            return;
        ToolTip.SetTip(this, string.IsNullOrEmpty(Shortcut) ? Label : $"{Label} ({Shortcut})");
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsLarge
    {
        get => GetValue(IsLargeProperty);
        set => SetValue(IsLargeProperty, value);
    }

    public int Reduction
    {
        get => GetValue(ReductionProperty);
        set => SetValue(ReductionProperty, value);
    }

    /// <summary>Shown as a large button right now (designed large and not reduced).</summary>
    public bool IsShownLarge => IsLarge && Reduction == 0;

    /// <summary>Line width on the 24x24 grid; small icons in a circle are scaled down a lot, so their lines are drawn bolder.</summary>
    private double _strokeThickness = 1.6;

    private Path MakePath(Geometry data, bool accent, bool tint = false)
    {
        var path = new Path
        {
            Data = data,
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            StrokeThickness = _strokeThickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        if (tint)
        {
            // Soft fill of the icon body; colour (Icon.Tint) and opacity come from the style, so disabled buttons dim it too
            path.Classes.Add("tint");
            path.StrokeThickness = 0;
        }
        else if (accent)
        {
            path.Classes.Add("accent");
            // On a filled (active) circle the style draws all lines white
            if (IconColor != null && !IsActive)
                path.Stroke = IconColor;
        }
        return path;
    }

    private void Rebuild()
    {
        var large = IsShownLarge;
        var showLabel = Reduction < 2 || Icon == null;
        // Ribbon buttons (automatic size) draw their icon in a round frame, like round icon buttons; an explicit size means a
        // bare icon unless a circle size is given too
        var round = (IconSize <= 0 || CircleSize > 0) && Icon != null;
        var size = IconSize > 0 ? IconSize : large ? 22 : 13;
        var circle = CircleSize > 0 ? CircleSize : large ? 38 : 22;
        _strokeThickness = round && size < 16 ? 2.1 : 1.6;
        var panel = new StackPanel
        {
            Orientation = large ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = large ? 5 : round ? 6 : 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Icon != null)
        {
            // Both layers share the 24x24 design grid, so they line up exactly; the Viewbox scales them together
            var layers = new Grid { Width = 24, Height = 24 };
            if (IconColor != null)
                layers.Children.Add(MakePath(Icon, accent: false, tint: true));
            layers.Children.Add(MakePath(Icon, accent: false));
            if (IconAccent != null)
                layers.Children.Add(MakePath(IconAccent, accent: true));
            var icon = new Viewbox { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = layers };
            if (round)
            {
                var frame = new Border
                {
                    Width = circle,
                    Height = circle,
                    CornerRadius = new CornerRadius(circle / 2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = icon,
                };
                frame.Classes.Add("iconCircle");
                panel.Children.Add(frame);
            }
            else
                panel.Children.Add(icon);
        }
        if (!string.IsNullOrEmpty(Label) && showLabel)
        {
            // Large labels wrap between words only; a single long word ("Zusammenführen…") widens the button instead of breaking
            var wrap = large && Label.Contains(' ');
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxWidth = wrap ? 88 : double.PositiveInfinity, VerticalAlignment = VerticalAlignment.Center });
        }
        Content = panel;
        UpdateToolTip();

        MinWidth = large ? 64 : 0;
        // A fixed circle is the whole button (quick guide)
        Padding = CircleSize > 0 ? new Thickness(0) : large ? new Thickness(6, 4, 6, 3) : round ? new Thickness(4, 1, 8, 1) : new Thickness(8, 3);
        HorizontalContentAlignment = large || CircleSize > 0 ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        Classes.Set("large", large);
        Classes.Set("round", round);
    }
}
