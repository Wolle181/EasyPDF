using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Settings;

namespace EasyPDF.App.Views;

/// <summary>
/// A window of the desktop program: the <see cref="MainView"/> of one document plus what only a window has (size and
/// position that are remembered, asking about unsaved changes when it is closed, the title).
/// </summary>
public sealed class MainWindow : Window
{
    private readonly AppSettings _settings;
    private bool _closeConfirmed;

    public MainWindow() : this(new AppSettings())
    {
    }

    /// <param name="windows">All main windows of the program; null: a window on its own (tests).</param>
    public MainWindow(AppSettings settings, WindowManager? windows = null)
    {
        _settings = settings;
        Width = 1100;
        Height = 700;
        MinWidth = 700;
        MinHeight = 400;
        View = new MainView(settings, windows);
        Content = View;
        // The window finds the view's named parts (FindControl) as if they were its own
        NameScope.SetNameScope(this, NameScope.GetNameScope(View));
        DataContext = View.ViewModel;
        Title = View.ViewModel.Title;
        View.ViewModel.PropertyChanged += OnViewModelChanged;
        // An area copied in another window can be pasted here: the Paste command asks the shared clipboard again
        Activated += (_, _) => View.OnHostActivated();
        View.CloseRequested += () =>
        {
            _closeConfirmed = true;
            Close();
        };
        RestoreWindowState();
    }

    public MainView View { get; }

    public MainViewModel ViewModel => View.ViewModel;

    public Task OpenPathAsync(string path) => View.OpenPathAsync(path);

    public void ShowBrandLogo(Avalonia.Media.Imaging.Bitmap? logo) => View.ShowBrandLogo(logo);

    public string? ShortcutFor(ICommand? command) => View.ShortcutFor(command);

    public Task EditTemplatesAsync() => View.EditTemplatesAsync();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Title))
            Title = View.ViewModel.Title;
    }

    private void RestoreWindowState()
    {
        if (_settings.WindowWidth is > 300 && _settings.WindowHeight is > 200)
        {
            Width = _settings.WindowWidth.Value;
            Height = _settings.WindowHeight.Value;
        }
        if (_settings.WindowX is { } x && _settings.WindowY is { } y && Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(x + 50, y + 50))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>A new window: same size, slightly offset from <paramref name="other"/> (maximized if that one is).</summary>
    public void PlaceNextTo(MainWindow other)
    {
        if (other.WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
            return;
        }
        WindowState = WindowState.Normal;
        Width = other.Width;
        Height = other.Height;
        var offset = (int)Math.Round(30 * other.DesktopScaling);
        var position = new PixelPoint(other.Position.X + offset, other.Position.Y + offset);
        // Back to the top left corner of the screen once the windows would run off it
        if (other.Screens.ScreenFromWindow(other) is { } screen && !screen.WorkingArea.Contains(position + new PixelPoint(200, 200)))
            position = screen.WorkingArea.Position;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = position;
    }

    /// <summary>Closing would lose changes the user has not decided about yet.</summary>
    public bool NeedsCloseConfirmation => !_closeConfirmed && ViewModel.IsDirty;

    /// <summary>Closes the window after asking about unsaved changes; false if the user cancelled.</summary>
    public async Task<bool> CloseAfterConfirmAsync()
    {
        if (NeedsCloseConfirmation)
        {
            Activate();
            if (!await ViewModel.ConfirmDiscardAsync())
                return false;
        }
        CloseWindow();
        return true;
    }

    public void CloseWindow()
    {
        _closeConfirmed = true;
        Close();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (NeedsCloseConfirmation)
        {
            e.Cancel = true;
            if (await ViewModel.ConfirmDiscardAsync())
            {
                _closeConfirmed = true;
                Close();
            }
            return;
        }

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
        }
        try
        {
            _settings.Save();
        }
        catch (Exception)
        {
            // Not being able to save settings must never block closing
        }
    }

    /// <summary>A closed window keeps no file open: the other windows keep the process (and so its handles) alive.</summary>
    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            await ViewModel.ReleaseDocumentAsync();
        }
        catch (Exception)
        {
            // Nothing to tell the user: the window is gone
        }
    }
}
