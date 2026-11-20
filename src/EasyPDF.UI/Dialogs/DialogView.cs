using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EasyPDF.App.Dialogs;

/// <summary>
/// The content of a dialog, without the window around it: the desktop program shows it in a <see cref="DialogWindow"/>,
/// the web version (which has no windows) as an overlay on the page (<see cref="Views.DialogLayer"/>). The dialog ends
/// with <see cref="Close"/>; its result is what the window's <c>ShowDialog</c> or the overlay returns. Closing the window
/// with its [X] (or Esc in the overlay without a cancel button) gives null, so results are defined with "null = cancel".
/// </summary>
public class DialogView : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<DialogView, string?>(nameof(Title));

    public static readonly StyledProperty<double> DialogWidthProperty = AvaloniaProperty.Register<DialogView, double>(nameof(DialogWidth), 460);

    /// <summary>NaN: as high as the content.</summary>
    public static readonly StyledProperty<double> DialogHeightProperty = AvaloniaProperty.Register<DialogView, double>(nameof(DialogHeight), double.NaN);

    public static readonly StyledProperty<bool> CanResizeProperty = AvaloniaProperty.Register<DialogView, bool>(nameof(CanResize));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double DialogWidth
    {
        get => GetValue(DialogWidthProperty);
        set => SetValue(DialogWidthProperty, value);
    }

    public double DialogHeight
    {
        get => GetValue(DialogHeightProperty);
        set => SetValue(DialogHeightProperty, value);
    }

    public bool CanResize
    {
        get => GetValue(CanResizeProperty);
        set => SetValue(CanResizeProperty, value);
    }

    /// <summary>Raised by <see cref="Close"/> with the result.</summary>
    public event Action<object?>? CloseRequested;

    /// <summary>The dialog is shown (window opened or overlay attached): set the focus here.</summary>
    public event EventHandler? Shown;

    /// <summary>The dialog is gone (closed by <see cref="Close"/>, by the window's [X] or by Esc in the overlay).</summary>
    public event EventHandler? Closed;

    public void Close(object? result = null) => CloseRequested?.Invoke(result);

    internal void RaiseShown() => Shown?.Invoke(this, EventArgs.Empty);

    internal void RaiseClosed() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>The top level the dialog is shown in (window or page), for pickers and the clipboard.</summary>
    protected TopLevel? Host => TopLevel.GetTopLevel(this);
}

/// <summary>A window around a <see cref="DialogView"/> (desktop).</summary>
public class DialogWindow : Window
{
    public DialogWindow(DialogView view)
    {
        DialogContent = view;
        Content = view;
        Width = view.DialogWidth;
        if (double.IsNaN(view.DialogHeight))
            SizeToContent = SizeToContent.Height;
        else
            Height = view.DialogHeight;
        CanResize = view.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Title = view.Title;
        view.PropertyChanged += (_, e) =>
        {
            if (e.Property == DialogView.TitleProperty)
                Title = view.Title;
        };
        // The window finds the view's named parts (FindControl) as if they were its own
        if (NameScope.GetNameScope(view) is { } scope)
            NameScope.SetNameScope(this, scope);
        view.CloseRequested += Close;
        Opened += (_, _) => view.RaiseShown();
        Closed += (_, _) => view.RaiseClosed();
    }

    public DialogView DialogContent { get; }
}
