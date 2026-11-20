using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using EasyPDF.App.Dialogs;

namespace EasyPDF.App.Views;

/// <summary>
/// Dialogs as overlays on the page (web version, which has no windows): a dimmed layer over everything with the dialog in
/// the middle, a title bar like a window's. Dialogs opened from a dialog stack up; only the top one takes input.
/// </summary>
public sealed class DialogLayer : Panel
{
    private readonly List<(Control Frame, DialogView View)> _open = [];

    public DialogLayer()
    {
        IsVisible = false;
        ZIndex = 1000;
    }

    public bool HasOpenDialog => _open.Count > 0;

    public Task<object?> ShowAsync(DialogView view)
    {
        var done = new TaskCompletionSource<object?>();
        var title = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(16, 10, 16, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = view.Title,
        };
        view.PropertyChanged += (_, e) =>
        {
            if (e.Property == DialogView.TitleProperty)
                title.Text = view.Title;
        };
        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(title, Dock.Top);
        body.Children.Add(title);
        body.Children.Add(new ScrollViewer { Content = view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var frame = new Border
        {
            Child = body,
            Width = view.DialogWidth,
            MaxHeight = 900,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, OffsetY = 6, Color = Color.FromArgb(90, 0, 0, 0) }),
        };
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("Window.Background"));
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Card.Border"));
        if (!double.IsNaN(view.DialogHeight))
            frame.Height = view.DialogHeight;
        // The dimmed area takes the clicks (nothing below can be used while a dialog is open)
        var shade = new Border { Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), Child = frame };
        shade.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled && _open.Count > 0 && _open[^1].View == view)
            {
                e.Handled = true;
                view.Close(null);
            }
        };
        void OnClose(object? result)
        {
            view.CloseRequested -= OnClose;
            Children.Remove(shade);
            _open.RemoveAll(o => o.View == view);
            IsVisible = _open.Count > 0;
            if (_open.Count > 0)
                _open[^1].Frame.IsEnabled = true; // the dialog below takes input again
            body.Children.Clear(); // the view may be shown again elsewhere
            view.RaiseClosed();
            done.TrySetResult(result);
        }
        view.CloseRequested += OnClose;
        // Only the top dialog takes input (its default/cancel buttons react to Enter/Esc on the whole page)
        if (_open.Count > 0)
            _open[^1].Frame.IsEnabled = false;
        _open.Add((shade, view));
        Children.Add(shade);
        IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            view.RaiseShown();
            if (TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() is not Visual focused || !view.IsVisualAncestorOf(focused))
                view.GetVisualDescendants().OfType<InputElement>()
                    .FirstOrDefault(c => c.Focusable && c.IsEffectivelyEnabled && c.IsEffectivelyVisible)?.Focus();
        });
        return done.Task;
    }
}
