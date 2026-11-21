using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.Styles;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Stamps;
using EasyPDF.Core.Templates;

namespace EasyPDF.App.Views;

/// <summary>
/// The whole user interface of one document: brand header, ribbon, pages, status bar. The desktop program shows it in a
/// <see cref="MainWindow"/>, the web version as the page itself. It is also the <see cref="IDialogService"/> of its view
/// model: dialogs are windows on the desktop and overlays in the browser (<see cref="ShowDialogAsync{T}"/>).
/// </summary>
public partial class MainView : UserControl, IDialogService, IShortcutProvider
{
    private readonly AppSettings _settings;
    private readonly WindowManager? _windows;
    private readonly MainViewModel _viewModel;

    public MainView() : this(new AppSettings())
    {
    }

    /// <param name="windows">All main windows of the program; null: a view on its own (tests, web version).</param>
    public MainView(AppSettings settings, WindowManager? windows = null)
    {
        _settings = settings;
        _windows = windows;
        InitializeComponent();
        ShowBrandLogo(BrandLogo.Image);
        _viewModel = new MainViewModel(settings, this, windows: windows);
        DataContext = _viewModel;
        _viewModel.PageSizesLoaded += () =>
        {
            PageView.RefreshPageSizes();
            OrganizeView.RefreshPageSizes();
            SidebarView.RefreshPageSizes();
        };
        _viewModel.ScrollToPageRequested += page =>
        {
            PageView.ScrollToPage(page);
            OrganizeView.ScrollToPage(page);
        };
        OrganizeView.SelectionRequested += ids => _viewModel.SetSelection(ids);
        OrganizeView.MoveRequested += (indices, insertIndex) => _viewModel.MovePages(indices, insertIndex);
        OrganizeView.PageActivated += page => _viewModel.SwitchModeCommand.Execute(page);
        SidebarView.PageClicked += page => _viewModel.GoToPage(page);
        // The recent files are shared: every window updates its lists
        _viewModel.RecentFilesChanged += () =>
        {
            foreach (var window in AllViews)
                window.RebuildRecentFiles();
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.PendingStampPath) or nameof(MainViewModel.PendingPaste))
                ApplyPendingStamp();
            else if (e.PropertyName is nameof(MainViewModel.IsTextToolActive))
                PageView.IsTextToolActive = _viewModel.IsTextToolActive;
            else if (e.PropertyName is nameof(MainViewModel.IsSelectTextToolActive))
            {
                PageView.IsSelectTextToolActive = _viewModel.IsSelectTextToolActive;
                if (_viewModel.IsSelectTextToolActive)
                    PageView.Focus(); // Esc, Ctrl+A and Ctrl+C go to the view
            }
            else if (e.PropertyName is nameof(MainViewModel.ActiveRedaction))
            {
                PageView.ActiveRedaction = _viewModel.ActiveRedaction;
                if (_viewModel.ActiveRedaction != null)
                    PageView.Focus(); // Esc goes to the view
            }
            else if (e.PropertyName is nameof(MainViewModel.IsCopyAreaToolActive))
            {
                PageView.IsCopyAreaToolActive = _viewModel.IsCopyAreaToolActive;
                if (_viewModel.IsCopyAreaToolActive)
                    PageView.Focus(); // Esc goes to the view
            }
            else if (e.PropertyName is nameof(MainViewModel.TextColor) or nameof(MainViewModel.TextFont) or nameof(MainViewModel.TextFontSize))
                ApplyTextStyle();
        };
        PageView.RedactionPlaceRequested += (areas, fill) => _viewModel.PlaceRedactions(areas, fill);
        PageView.RedactionChangeRequested += (id, rect) => _viewModel.ChangeRedaction(id, rect);
        _viewModel.RedactSelectionRequested += fill => PageView.RedactSelectedText(fill);
        PageView.AreaCopied += area => _viewModel.AreaCopied(area);
        PageView.AreaCopyFailed += ex => _ = ShowErrorAsync(Loc.T("ErrCopyAreaFailed"), ex.Message);
        PageView.StampPlaceRequested += OnStampPlaceRequested;
        PageView.TextPlaceRequested += (page, rect, text) => _viewModel.PlaceText(page, rect, text);
        PageView.TextChangeRequested += (page, id, rect, text) => _viewModel.ChangeText(page, id, rect, text);
        // A click of the text tool opens the editor: the tool is used up (Esc then belongs to the editor)
        PageView.TextEditorOpened += () => _viewModel.IsTextToolActive = false;
        ApplyTextStyle();
        PageView.StampChangeRequested += (id, rect) => _viewModel.ChangeStamp(id, rect);
        PageView.StampDateEditRequested += id => _ = ChangeStampDateAsync(id);
        PageView.TextBoxChangeRequested += (id, rect, size) => _viewModel.ChangeTextBox(id, rect, size);
        PageView.StampDeleteRequested += id => _viewModel.RemoveStamp(id);
        // The Delete shortcut (delete pages) removes the selected stamp instead while one is selected
        _viewModel.DeleteSelectedStamp = () => PageView.DeleteSelectedStamp();
        PageView.StampCancelRequested += () => _viewModel.CancelStampCommand.Execute(null);
        PageView.TextSelectionChanged += text => _viewModel.SetTextSelection(text);
        PageView.PageWithoutTextClicked += page => _viewModel.ReportPageWithoutText(page);
        _viewModel.SelectAllTextRequested += () => _ = PageView.SelectAllTextAsync();
        _viewModel.ClearTextSelectionRequested += () => PageView.ClearTextSelection();
        _viewModel.GoToPageBoxRequested += () => StatusPageButton.Flyout?.ShowAt(StatusPageButton);
        _viewModel.SearchBoxFocusRequested += FocusSearchBox;
        _viewModel.SearchHitShown += hit => PageView.ScrollToSearchHit(hit);
        // Tunnel: before the window's shortcuts (Esc) and before the box itself
        SearchBox.AddHandler(KeyDownEvent, OnSearchBoxKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // The splitter reports how far the pointer is from where it was pressed on it; the sidebar follows it, up to half the window
        SidebarSplitter.DragDelta += (_, e) => _viewModel.ResizePageSidebar(PageSidebar.Bounds.Width + e.Vector.X, Bounds.Width / 2);

        AddHandler(DragDrop.DropEvent, OnDrop);
        if (PlatformFeatures.Current.IsWeb)
        {
            BrandSubtitle.Text = Loc.T("BrandSubtitleWeb");
            EInvoiceOpenButton.IsVisible = false; // a browser cannot open it in a program of the system: Save downloads it
        }
        ZoomBox.LostFocus += (_, _) => _viewModel.ApplyZoomInputCommand.Execute(null);
        BuildMenuAndShortcuts();
        RebuildRecentFiles();
    }

    public MainViewModel ViewModel => _viewModel;

    /// <summary>The window around the view (desktop), or null (web version: the page has no windows).</summary>
    public Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private TopLevel Host => TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("The view is not shown.");

    /// <summary>Owner for the dialogs that exist as windows only (features the web version does not offer).</summary>
    private Window RequireWindow() => OwnerWindow ?? throw new NotSupportedException("This dialog needs a window.");

    /// <summary>The host window came to the front: an area copied in another window can be pasted here now.</summary>
    public void OnHostActivated() => _viewModel.PasteAreaCommand.NotifyCanExecuteChanged();

    /// <summary>The view wants its window closed (the user confirmed or there was nothing to confirm).</summary>
    public event Action? CloseRequested;

    private readonly List<KeyBinding> _shortcuts = [];
    private List<KeyBinding> _appliedShortcuts = [];
    private TopLevel? _shortcutHost;
    private NativeMenu? _nativeMenu;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyHostMenuAndShortcuts();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_shortcutHost != null)
            foreach (var binding in _appliedShortcuts)
                _shortcutHost.KeyBindings.Remove(binding);
        _appliedShortcuts = [];
        _shortcutHost = null;
    }

    /// <summary>
    /// The shortcuts belong to the window (or the page), not to the view: they must work while nothing inside has the
    /// focus. The menu goes to the window (native menu bar on macOS, NativeMenuBar elsewhere).
    /// </summary>
    private void ApplyHostMenuAndShortcuts()
    {
        if (TopLevel.GetTopLevel(this) is not { } host)
            return;
        if (_shortcutHost != null)
            foreach (var binding in _appliedShortcuts)
                _shortcutHost.KeyBindings.Remove(binding);
        host.KeyBindings.AddRange(_shortcuts);
        _appliedShortcuts = [.. _shortcuts];
        _shortcutHost = host;
        if (host is Window window && _nativeMenu != null)
            NativeMenu.SetMenu(window, _nativeMenu);
    }

    private void FocusSearchBox()
    {
        void Focus()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        if (SearchBox.IsEffectivelyVisible)
            Focus();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(Focus); // the bar was just switched on
    }

    /// <summary>Enter: next hit, Shift+Enter: previous one, Esc: closes the search and gives the focus back to the pages.</summary>
    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers & ~KeyModifiers.Shift;
        if (e.Key == Key.Enter && mods == KeyModifiers.None)
            (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _viewModel.FindPreviousCommand : _viewModel.FindNextCommand).Execute(null);
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            _viewModel.CloseSearchCommand.Execute(null);
            if (_viewModel.OrganizeVisible)
                OrganizeView.Focus();
            else
                PageView.Focus();
        }
        else
            return;
        e.Handled = true;
    }

    private IEnumerable<MainView> AllViews => _windows?.Windows.Select(w => w.View) ?? [this];

    public Task OpenPathAsync(string path) => _viewModel.OpenPathAsync(path);

    /// <summary>Brand header: the logo (icon made from a logo file) or, for null, the mark drawn in XAML.</summary>
    public void ShowBrandLogo(Avalonia.Media.Imaging.Bitmap? logo)
    {
        BrandLogoImage.Source = logo;
        BrandLogoImage.IsVisible = logo != null;
        BrandMark.IsVisible = logo == null;
    }

    #region Menu, shortcuts, recent files

    private sealed record Entry(string HeaderKey, ICommand Command, Key Key = Key.None, bool Primary = false, bool Shift = false);

    private static KeyModifiers PrimaryModifier =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    private static KeyGesture GestureOf(Entry e)
    {
        var mods = (e.Primary ? PrimaryModifier : KeyModifiers.None) | (e.Shift ? KeyModifiers.Shift : KeyModifiers.None);
        return new KeyGesture(e.Key, mods);
    }

    /// <summary>
    /// Keys a text box uses itself: typing and moving (no modifier, except the function keys) and its edit shortcuts
    /// (copy, undo, select all…).
    /// </summary>
    private static bool IsTextBoxGesture(KeyGesture gesture)
    {
        var mods = gesture.KeyModifiers & ~KeyModifiers.Shift;
        return (mods == KeyModifiers.None && gesture.Key is not (>= Key.F1 and <= Key.F24))
            || (mods == PrimaryModifier && gesture.Key is Key.A or Key.C or Key.X or Key.V or Key.Z or Key.Y
                or Key.Home or Key.End or Key.Left or Key.Right or Key.Back or Key.Delete);
    }

    /// <summary>
    /// A window shortcut on a key a text box uses itself (see <see cref="IsTextBoxGesture"/>): while a text box has the
    /// focus, the key belongs to the box (Delete in the zoom box must not delete pages, Ctrl+C copies the typed text).
    /// </summary>
    private KeyBinding ShortcutBinding(KeyGesture gesture, ICommand command) => new()
    {
        Gesture = gesture,
        Command = IsTextBoxGesture(gesture)
            ? new YieldingCommand(command, () => IsDialogOpen || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            : new YieldingCommand(command, () => IsDialogOpen),
    };

    private sealed class YieldingCommand(ICommand inner, Func<bool> yield) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => inner.CanExecuteChanged += value;
            remove => inner.CanExecuteChanged -= value;
        }

        public bool CanExecute(object? parameter) => !yield() && inner.CanExecute(parameter);

        public void Execute(object? parameter)
        {
            if (!yield())
                inner.Execute(parameter);
        }
    }

    private NativeMenu? _recentMenu;
    private Dictionary<ICommand, string> _shortcutTexts = [];

    public string? ShortcutFor(ICommand? command) =>
        command != null && _shortcutTexts.TryGetValue(command, out var text) ? text : null;

    /// <summary>Menu bar (native on macOS) and keyboard shortcuts come from the same table, so they always agree.</summary>
    private void BuildMenuAndShortcuts()
    {
        // Windows/Linux: the menu bar in the window can be switched off (every command is in the ribbon as well);
        // macOS has its global menu bar, which always stays
        MenuBar.IsVisible = !PlatformFeatures.Current.IsWeb && (_settings.ShowMenuBar || OperatingSystem.IsMacOS());
        var vm = _viewModel;
        var file = new[]
        {
            new Entry("MenuNewWindow", vm.NewWindowCommand, Key.N, Primary: true),
            new Entry("MenuOpen", vm.OpenCommand, Key.O, Primary: true),
            null,
            new Entry("MenuSave", vm.SaveCommand, Key.S, Primary: true),
            new Entry("MenuSaveAs", vm.SaveAsCommand, Key.S, Primary: true, Shift: true),
            null,
            new Entry("MenuPrint", vm.PrintCommand, Key.P, Primary: true),
            new Entry("MenuSendByMail", vm.SendByMailCommand),
            null,
            new Entry("MenuConcatenate", vm.ConcatenateCommand),
            new Entry("MenuExtract", vm.ExtractPagesCommand),
            new Entry("MenuSplit", vm.SplitDocumentCommand),
            new Entry("MenuReduceSize", vm.ReduceSizeCommand),
            new Entry("MenuAttachments", vm.ShowAttachmentsCommand),
            null,
            new Entry("MenuSettings", vm.ShowSettingsCommand, Key.OemComma, Primary: true),
            null,
            new Entry("MenuClose", vm.CloseDocumentCommand, Key.W, Primary: true),
            new Entry("MenuExit", vm.ExitCommand),
        };
        var edit = new[]
        {
            new Entry("MenuUndo", vm.UndoCommand, Key.Z, Primary: true),
            new Entry("MenuRedo", vm.RedoCommand, Key.Y, Primary: true),
            null,
            new Entry("MenuInsertBefore", vm.InsertBeforeCommand),
            new Entry("MenuInsertAfter", vm.InsertAfterCommand),
            new Entry("MenuInsertBlankBefore", vm.InsertBlankBeforeCommand),
            new Entry("MenuInsertBlankAfter", vm.InsertBlankAfterCommand),
            new Entry("MenuReplacePages", vm.ReplacePagesCommand),
            null,
            new Entry("MenuRotate", vm.RotatePagesCommand, Key.R, Primary: true, Shift: true),
            new Entry("MenuRotateLeft", vm.RotateLeftCommand, Key.L, Primary: true),
            new Entry("MenuRotateRight", vm.RotateRightCommand, Key.R, Primary: true),
            new Entry("MenuDeletePages", vm.DeletePagesCommand, Key.Delete),
            null,
            new Entry("MenuRecognizeText", vm.RecognizeTextCommand),
            new Entry("MenuSelectText", vm.ToggleSelectTextToolCommand),
            new Entry("MenuCopyText", vm.CopyTextCommand, Key.C, Primary: true),
            null,
            new Entry("MenuFind", vm.ShowSearchCommand, Key.F, Primary: true),
            new Entry("MenuFindNext", vm.FindNextCommand, Key.F3),
            new Entry("MenuFindPrevious", vm.FindPreviousCommand, Key.F3, Shift: true),
            null,
            new Entry("MenuRedact", vm.RedactCommand),
            new Entry("MenuCover", vm.CoverCommand),
            null,
            new Entry("MenuCopyArea", vm.ToggleCopyAreaToolCommand),
            new Entry("MenuPasteArea", vm.PasteAreaCommand, Key.V, Primary: true),
            null,
            new Entry("MenuSelectAll", vm.SelectAllCommand, Key.A, Primary: true),
        };
        var view = new[]
        {
            new Entry("MenuReadingMode", vm.ShowReadingCommand, Key.D1, Primary: true),
            new Entry("MenuOrganizeMode", vm.ShowOrganizeCommand, Key.D2, Primary: true),
            null,
            new Entry("MenuShowPageSidebar", vm.TogglePageSidebarCommand, Key.T, Primary: true),
            null,
            new Entry("MenuFirstPage", vm.GoToFirstPageCommand, Key.Home, Primary: true),
            new Entry("MenuLastPage", vm.GoToLastPageCommand, Key.End, Primary: true),
            new Entry("MenuGoToPage", vm.ShowGoToPageCommand, Key.G, Primary: true),
            null,
            new Entry("MenuFitHeight", vm.FitHeightCommand),
            new Entry("MenuFitWidth", vm.FitWidthCommand),
            new Entry("MenuFitBest", vm.FitBestCommand),
            null,
            new Entry("MenuZoomIn", vm.ZoomInCommand, Key.OemPlus, Primary: true),
            new Entry("MenuZoomOut", vm.ZoomOutCommand, Key.OemMinus, Primary: true),
            new Entry("MenuActualSize", vm.ActualSizeCommand, Key.D0, Primary: true),
            null,
            new Entry("MenuToggleRibbon", vm.ToggleRibbonCommand, Key.F1, Primary: true),
        };
        var help = new[] { new Entry("MenuQuickGuide", vm.ShowQuickGuideCommand, Key.F1), IntroVideo.IsAvailable ? new Entry("MenuIntroVideo", vm.ShowIntroVideoCommand) : null,
                           new Entry("MenuAbout", vm.ShowAboutCommand) }.OfType<Entry>();

        var root = new NativeMenu();
        // null entries are separators
        NativeMenuItem Top(string key, IEnumerable<Entry?> entries, NativeMenuItem? extra = null)
        {
            var menu = new NativeMenu();
            foreach (var e in entries)
            {
                if (e == null)
                {
                    menu.Add(new NativeMenuItemSeparator());
                    continue;
                }
                var item = new NativeMenuItem { Header = Loc.T(e.HeaderKey), Command = e.Command };
                if (e.Key != Key.None)
                    item.Gesture = GestureOf(e);
                menu.Add(item);
                if (e.HeaderKey == "MenuOpen" && extra != null)
                    menu.Add(extra);
            }
            return new NativeMenuItem { Header = Loc.T(key), Menu = menu };
        }

        _recentMenu = [];
        var recent = new NativeMenuItem { Header = Loc.T("MenuOpenRecent"), Menu = _recentMenu };
        root.Add(Top("MenuFile", file, recent));
        root.Add(Top("MenuEdit", edit));
        root.Add(Top("MenuView", view));
        root.Add(Top("MenuHelp", help));
        _nativeMenu = root;

        _shortcuts.Clear();
        var withShortcut = file.Concat(edit).Concat(view).Concat(help).OfType<Entry>().Where(e => e.Key != Key.None && IsAvailable(e.Command)).ToList();
        foreach (var e in withShortcut)
            _shortcuts.Add(ShortcutBinding(GestureOf(e), e.Command));
        // Ribbon tooltips show the shortcut of the button's command
        // (buttons ask for it when they join the tree, because only the selected ribbon tab exists at a time)
        _shortcutTexts = withShortcut
            .GroupBy(e => e.Command)
            .ToDictionary(g => g.Key, g => GestureOf(g.First()).ToString("p", null)); // "p": "Ctrl+2" instead of "Ctrl+D2", macOS symbols
        foreach (var button in this.GetLogicalDescendants().OfType<RibbonButton>())
            button.Shortcut ??= ShortcutFor(button.Command);
        // Alternative zoom/redo shortcuts
        _shortcuts.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new YieldingCommand(vm.CancelOperationCommand, () => IsDialogOpen) });
        _shortcuts.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new YieldingCommand(vm.CancelStampCommand, () => IsDialogOpen) });
        _shortcuts.Add(new KeyBinding { Gesture = new KeyGesture(Key.Add, PrimaryModifier), Command = new YieldingCommand(vm.ZoomInCommand, () => IsDialogOpen) });
        _shortcuts.Add(new KeyBinding { Gesture = new KeyGesture(Key.Subtract, PrimaryModifier), Command = new YieldingCommand(vm.ZoomOutCommand, () => IsDialogOpen) });
        _shortcuts.Add(ShortcutBinding(new KeyGesture(Key.Z, PrimaryModifier | KeyModifiers.Shift), vm.RedoCommand));
        PageView.ContextMenu = BuildPageContextMenu(() => PageView.ContextPage);
        StatusZoomMenu.Flyout = BuildZoomMenu();
        StatusPageButton.Flyout = BuildGoToPageFlyout();
        StampButton.Flyout = BuildStampGallery();
        TextStyleButton.Flyout = BuildTextStyleFlyout();
        TemplatesButton.Flyout = BuildTemplatesFlyout();
        PageView.TemplateProvider = page => vm.ExpandPendingTemplate(page);
        OrganizeView.ContextMenu = BuildPageContextMenu(() => OrganizeView.ContextPage);
        RebuildRecentFiles();
        ApplyHostMenuAndShortcuts();
    }

    /// <summary>Box above the "Page x of y" text of the status bar: type a page number, Enter jumps there.</summary>
    private Flyout BuildGoToPageFlyout()
    {
        var vm = _viewModel;
        var box = new TextBox { Width = 90, MinHeight = 26, Padding = new Thickness(6, 2), HorizontalContentAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        flyout.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { label, box } };
        flyout.Opened += (_, _) =>
        {
            label.Text = Loc.F("GoToPagePrompt", vm.Document?.PageCount ?? 0);
            box.Text = (vm.CurrentPage + 1).ToString();
            box.Focus();
            box.SelectAll();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            vm.GoToTypedPageCommand.Execute(box.Text);
            flyout.Hide();
            e.Handled = true;
        };
        return flyout;
    }

    /// <summary>
    /// Flyout of the Templates button: the templates of the templates folder (read each time it opens, so files added with
    /// the file manager show up) and the entries to edit them and to open the folder.
    /// </summary>
    private Flyout BuildTemplatesFlyout()
    {
        var host = new StackPanel { Spacing = 2, MinWidth = 220 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Content = new ScrollViewer { MaxHeight = 420, Content = host } };
        flyout.Opening += (_, _) =>
        {
            host.Children.Clear();
            var templates = _viewModel.LoadTemplates();
            if (templates.Count == 0)
                host.Children.Add(new TextBlock { Text = Loc.T("TemplatesNone"), Opacity = 0.6, Margin = new Thickness(8) });
            foreach (var template in templates)
            {
                var item = new Button { Content = template.Name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Avalonia.Media.Brushes.Transparent };
                ToolTip.SetTip(item, template.Text);
                item.Click += (_, _) =>
                {
                    flyout.Hide();
                    _viewModel.SelectTemplateCommand.Execute(template);
                    PageView.Focus();
                };
                host.Children.Add(item);
            }
            var edit = new Button { Content = Loc.T("TemplatesEdit"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Avalonia.Media.Brushes.Transparent };
            edit.Click += async (_, _) =>
            {
                flyout.Hide();
                await EditTemplatesAsync();
            };
            var open = new Button { Content = Loc.T("TemplateOpenFolder"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Avalonia.Media.Brushes.Transparent };
            ToolTip.SetTip(open, _viewModel.TemplateFolder);
            open.Click += async (_, _) =>
            {
                flyout.Hide();
                await FolderLauncher.OpenAsync(RequireWindow(), _viewModel.TemplateFolder);
            };
            host.Children.Add(new Separator());
            host.Children.Add(edit);
            host.Children.Add(open);
        };
        return flyout;
    }

    public async Task EditTemplatesAsync()
    {
        var before = _viewModel.LoadTemplates();
        var result = await new TemplatesDialog(before).ShowDialog<List<TextTemplate>?>(RequireWindow());
        if (result != null)
            await _viewModel.SaveTemplatesAsync(before, result);
    }

    private void ApplyTextStyle()
    {
        PageView.PenColor = _viewModel.TextColor;
        PageView.PenFont = _viewModel.TextFont;
        PageView.PenSize = _viewModel.TextFontSize;
    }

    /// <summary>
    /// Hands the picture chosen in the stamp gallery, or the copied area to paste, to the reading view (null = placing ended).
    /// </summary>
    private void ApplyPendingStamp()
    {
        var old = PageView.PendingStampImage;
        var path = _viewModel.PendingStampPath;
        var paste = _viewModel.PendingPaste;
        Avalonia.Media.Imaging.Bitmap? bitmap = null;
        double? dpi = null;
        if (paste != null)
        {
            using var stream = new MemoryStream(paste.Png);
            bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
        }
        else if (path != null)
        {
            try
            {
                var data = File.ReadAllBytes(path);
                using var stream = new MemoryStream(StampPicture(data, _viewModel.CurrentPage));
                bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                dpi = StampLibrary.ReadDpi(data);
            }
            catch (Exception)
            {
                _viewModel.CancelStampCommand.Execute(null);
                _ = ShowErrorAsync(Loc.F("StampUnreadable", Path.GetFileName(path)));
                return;
            }
        }
        PageView.PendingStampSize = paste != null ? (paste.WidthPt, paste.HeightPt) : null;
        PageView.PendingStampDpi = dpi;
        PageView.PendingStampImage = bitmap;
        old?.Dispose();
        if (bitmap != null)
            PageView.Focus();
    }

    /// <summary>
    /// The picture of a stamp file as it would be placed on the page now: with the text of its fields, if it has any. Throws
    /// if it is no PNG, also under the name of one (a JPG renamed by hand): stamps are PNG only.
    /// </summary>
    private byte[] StampPicture(byte[] file, int pageIndex)
    {
        if (!StampFile.IsPng(file))
            throw new InvalidDataException("Not a PNG file.");
        return StampFile.ReadDefinition(file) is { } definition ? StampComposer.Compose(file, definition, _viewModel.MacroContext(pageIndex)).Png : file;
    }

    private void OnStampPlaceRequested(int pageIndex, EasyPDF.Core.Documents.RectPt viewRect)
    {
        if (_viewModel.PendingPaste != null)
        {
            _viewModel.PlacePastedArea(pageIndex, viewRect);
            return;
        }
        if (_viewModel.PendingStampPath is not { } path || PageView.PendingStampImage is not { } bitmap)
            return;
        byte[] image;
        ComposedStamp? composed = null;
        EasyPDF.Core.Documents.StampTemplate? template = null;
        try
        {
            image = File.ReadAllBytes(path); // the picture is copied into the document, later changes in the folder don't matter
            if (StampFile.ReadDefinition(image) is { } definition)
            {
                // The date and page are those of this click, and stay: a placed stamp doesn't change by itself
                template = new EasyPDF.Core.Documents.StampTemplate(image, definition, _viewModel.MacroContext(pageIndex));
                composed = StampComposer.Compose(image, definition, template.Context);
            }
        }
        catch (Exception)
        {
            // Like when loading the picture: the decoder can fail in many ways on a file that changed in the meantime
            _viewModel.CancelStampCommand.Execute(null);
            _ = ShowErrorAsync(Loc.F("StampUnreadable", Path.GetFileName(path)));
            return;
        }
        if (composed != null)
            _viewModel.PlaceStamp(pageIndex, viewRect, composed.Png, composed.PixelWidth, composed.PixelHeight, composed.Bgra, template);
        else
            _viewModel.PlaceStamp(pageIndex, viewRect, image, bitmap.PixelSize.Width, bitmap.PixelSize.Height, EasyPDF.App.Services.StampPixels.ToStraightBgra(bitmap));
    }

    /// <summary>
    /// The stamp editor on a PNG of the stamp folder, which the result replaces, or on a new stamp (null).
    /// </summary>
    private async Task EditStampAsync(string? path)
    {
        StampEditorDialog dialog;
        try
        {
            dialog = new StampEditorDialog(path, _viewModel.MacroContext(_viewModel.CurrentPage));
        }
        catch (Exception)
        {
            // Like when loading the picture: the decoder can fail in many ways on a file that changed in the meantime
            await ShowErrorAsync(Loc.F("StampUnreadable", Path.GetFileName(path ?? "")));
            return;
        }
        if (await dialog.ShowDialog<StampEditorResult?>(RequireWindow()) is not { } result)
            return;
        var folder = path != null ? Path.GetDirectoryName(path)! : StampLibrary.ResolveFolder(_settings);
        try
        {
            var saved = StampLibrary.SaveStamp(folder, result.Name, result.Png, path);
            _viewModel.ReportStampSaved(Path.GetFileNameWithoutExtension(saved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await ShowErrorAsync(Loc.F("StampSaveFailed", result.Name, ex.Message));
        }
    }

    /// <summary>Asks for another date for a placed stamp and makes its picture again; choosing the same date changes nothing.</summary>
    private async Task ChangeStampDateAsync(int stampId)
    {
        var stamp = _viewModel.Document?.Pages.SelectMany(p => p.Stamps).FirstOrDefault(s => s.Id == stampId);
        if (stamp?.Template is not { } template)
            return;
        var chosen = await new StampDateDialog(template.Context.Now).ShowDialog<DateTime?>(RequireWindow());
        if (chosen is not { } date || date.Date == template.Context.Now.Date)
            return;
        var changed = template with { Context = template.Context with { Now = date.Date + template.Context.Now.TimeOfDay } };
        var picture = StampComposer.Compose(template.Picture, template.Definition, changed.Context);
        if (_viewModel.ChangeStampDate(stampId, picture, changed) is { } result)
            PageView.SelectedStampId = result.Id;
    }

    /// <summary>Flyout of the pen button: colour swatches, free #RRGGBB entry, font and size of new text.</summary>
    private Flyout BuildTextStyleFlyout()
    {
        var vm = _viewModel;
        var swatches = new WrapPanel { MaxWidth = 176 };
        foreach (var hex in TextStyle.Palette)
        {
            var swatch = new Button
            {
                Width = 36,
                Height = 28,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex)),
                BorderBrush = Avalonia.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) => vm.TextColor = hex;
            swatches.Children.Add(swatch);
        }

        var hexBox = new TextBox { Text = vm.TextColor, Width = 100, MaxLength = 7 };
        hexBox.LostFocus += (_, _) =>
        {
            if (TextStyle.IsValidColor(hexBox.Text))
                vm.TextColor = TextStyle.NormalizeColor(hexBox.Text);
            hexBox.Text = vm.TextColor;
        };
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && TextStyle.IsValidColor(hexBox.Text))
                vm.TextColor = TextStyle.NormalizeColor(hexBox.Text);
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TextColor) && !hexBox.IsFocused)
                hexBox.Text = vm.TextColor;
        };

        var fontBox = new ComboBox { ItemsSource = TextStyle.Fonts, SelectedItem = vm.TextFont, MinWidth = 140 };
        fontBox.SelectionChanged += (_, _) =>
        {
            if (fontBox.SelectedItem is string font)
                vm.TextFont = font;
        };
        var sizeBox = new ComboBox { ItemsSource = TextStyle.Sizes, SelectedItem = TextStyle.Sizes.Contains(vm.TextFontSize) ? vm.TextFontSize : (object?)null, MinWidth = 140, PlaceholderText = vm.TextFontSize.ToString("0.#") };
        sizeBox.SelectionChanged += (_, _) =>
        {
            if (sizeBox.SelectedItem is double size)
                vm.TextFontSize = size;
        };

        static Control Row(string key, Control editor) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = Loc.T(key), Width = 70, VerticalAlignment = VerticalAlignment.Center }, editor },
        };
        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = Loc.T("TextPenColor"), FontWeight = Avalonia.Media.FontWeight.SemiBold },
                swatches,
                Row("TextColorCode", hexBox),
                Row("TextFont", fontBox),
                Row("TextSize", sizeBox),
            },
        };
        return new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Content = content };
    }

    private readonly List<Avalonia.Media.Imaging.Bitmap> _stampThumbnails = [];

    /// <summary>Subfolder sections of the stamp gallery the user opened (kept while the window is open).</summary>
    private readonly HashSet<string> _openStampGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gallery of the stamp button: thumbnails and names of the PNG files in the stamp folder, those of each subfolder in
    /// a section of their own. The folder is read each time the gallery opens, so new files show up without a restart.
    /// </summary>
    private Flyout BuildStampGallery()
    {
        var host = new StackPanel { Spacing = 8, MinWidth = 260 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, Content = new ScrollViewer { MaxHeight = 420, Content = host } };
        flyout.Opening += (_, _) => FillStampGallery(host, flyout);
        return flyout;
    }

    private void FillStampGallery(StackPanel host, Flyout flyout)
    {
        foreach (var thumb in _stampThumbnails)
            thumb.Dispose();
        _stampThumbnails.Clear();
        host.Children.Clear();

        var folder = StampLibrary.ResolveFolder(_settings);
        StampLibrary.TryEnsureFolder(folder);
        var skipped = 0;
        WrapPanel Tiles(IReadOnlyList<string> files)
        {
            var grid = new WrapPanel { MaxWidth = 440 };
            foreach (var path in files)
            {
                Avalonia.Media.Imaging.Bitmap thumb;
                try
                {
                    // A stamp with fields shows what it would look like today
                    using var stream = new MemoryStream(StampPicture(File.ReadAllBytes(path), _viewModel.CurrentPage));
                    thumb = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 160);
                }
                catch (Exception)
                {
                    // Any unreadable file is skipped: the decoder can fail in many ways on damaged pictures (it even throws NullReferenceException)
                    skipped++;
                    continue;
                }
                _stampThumbnails.Add(thumb);
                var item = new Button
                {
                    Classes = { "ribbon" },
                    Width = 120,
                    Margin = new Thickness(2),
                    Command = _viewModel.SelectStampCommand,
                    CommandParameter = path,
                    Content = new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new Image { Source = thumb, Width = 96, Height = 64, Stretch = Avalonia.Media.Stretch.Uniform },
                            new TextBlock { Text = Path.GetFileNameWithoutExtension(path), FontSize = 12, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center },
                        },
                    },
                };
                ToolTip.SetTip(item, Path.GetFileName(path));
                item.Click += (_, _) => flyout.Hide();
                var edit = new MenuItem { Header = Loc.T("StampEdit") };
                edit.Click += (_, _) =>
                {
                    flyout.Hide();
                    _ = EditStampAsync(path);
                };
                item.ContextMenu = new ContextMenu { Items = { edit } };
                grid.Children.Add(item);
            }
            return grid;
        }

        // Pictures directly in the folder first, then one section per subfolder (e.g. the number stamps); a section stays
        // open as the user left it, so picking 1, 2, 3 ... in a row needs no extra click
        var shown = 0;
        foreach (var group in StampLibrary.ListGroups(folder))
        {
            var grid = Tiles(group.Files);
            shown += grid.Children.Count;
            if (group.Name is not { } name)
            {
                if (grid.Children.Count > 0)
                    host.Children.Add(grid);
                continue;
            }
            if (grid.Children.Count == 0)
                continue;
            var section = new Expander
            {
                Header = Loc.F("StampGroupHeader", name, grid.Children.Count),
                Content = grid,
                IsExpanded = _openStampGroups.Contains(name),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            section.Expanded += (_, _) => _openStampGroups.Add(name);
            section.Collapsed += (_, _) => _openStampGroups.Remove(name);
            host.Children.Add(section);
        }
        if (shown == 0)
            host.Children.Add(new TextBlock { Text = Loc.T("StampGalleryEmpty"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 260 });
        if (skipped > 0)
            host.Children.Add(new TextBlock { Text = Loc.F("StampSkipped", skipped), Opacity = 0.7 });
        if (shown > 0)
            host.Children.Add(new TextBlock { Text = Loc.T("StampEditHint"), FontSize = 12, Opacity = 0.7 });
        host.Children.Add(new TextBlock { Text = Loc.T("StampPngHint"), FontSize = 12, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Left });
        var newStamp = new Button { Content = Loc.T("StampNew") };
        newStamp.Click += (_, _) =>
        {
            flyout.Hide();
            _ = EditStampAsync(null);
        };
        host.Children.Add(newStamp);
        var open = new Button { Content = Loc.T("StampOpenFolder") };
        ToolTip.SetTip(open, folder);
        open.Click += async (_, _) =>
        {
            flyout.Hide();
            await FolderLauncher.OpenAsync(RequireWindow(), folder);
        };
        host.Children.Add(open);
    }

    /// <summary>Zoom menu of the status bar: fixed steps (thumbnail size in Organize mode) and the fit modes.</summary>
    private MenuFlyout BuildZoomMenu()
    {
        var vm = _viewModel;
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (var percent in new double[] { 50, 75, 100, 125, 150, 200, 300, 400 })
            menu.Items.Add(new MenuItem { Header = Loc.F("StatusZoom", percent), Command = vm.ZoomToCommand, CommandParameter = percent });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Loc.T("BtnFitHeight"), Command = vm.FitHeightCommand });
        menu.Items.Add(new MenuItem { Header = Loc.T("BtnFitWidth"), Command = vm.FitWidthCommand });
        menu.Items.Add(new MenuItem { Header = Loc.T("BtnFitBest"), Command = vm.FitBestCommand });
        return menu;
    }

    /// <summary>The right-click menu of a page, identical in Reading and Organize mode; its commands receive the clicked page.</summary>
    private ContextMenu BuildPageContextMenu(Func<int> contextPage)
    {
        var vm = _viewModel;
        var paged = new List<MenuItem>();
        MenuItem Item(string key, ICommand command)
        {
            var item = new MenuItem { Header = Loc.T(key), Command = command };
            paged.Add(item);
            return item;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("CtxInsertBefore", vm.InsertBeforeCommand));
        menu.Items.Add(Item("CtxInsertAfter", vm.InsertAfterCommand));
        menu.Items.Add(Item("CtxInsertBlankBefore", vm.InsertBlankBeforeCommand));
        menu.Items.Add(Item("CtxInsertBlankAfter", vm.InsertBlankAfterCommand));
        menu.Items.Add(Item("CtxReplacePages", vm.ReplacePagesCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("BtnRotateLeft", vm.RotateLeftCommand));
        menu.Items.Add(Item("BtnRotateRight", vm.RotateRightCommand));
        menu.Items.Add(Item("CtxRotateDialog", vm.RotatePagesCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("CtxRecognizeText", vm.RecognizeTextCommand));
        menu.Items.Add(Item("CtxCopyText", vm.CopyTextCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("CtxExtract", vm.ExtractPagesCommand));
        menu.Items.Add(Item("CtxPrint", vm.PrintCommand));
        menu.Items.Add(Item("CtxDelete", vm.DeletePagesCommand));
        menu.Items.Add(new Separator());
        var switchItem = Item("CtxShowOrganize", vm.SwitchModeCommand);
        menu.Items.Add(switchItem);

        // The web version hides what it does not offer (and the separators that would then stand alone)
        foreach (var item in paged.Where(i => !IsAvailable(i.Command)))
            item.IsVisible = false;
        if (PlatformFeatures.Current.IsWeb)
            foreach (var separator in menu.Items.OfType<Separator>().ToList())
                menu.Items.Remove(separator);

        menu.Opening += (_, _) =>
        {
            var page = contextPage();
            foreach (var item in paged)
                item.CommandParameter = page;
            switchItem.Header = Loc.T(vm.IsOrganizeMode ? "CtxShowReading" : "CtxShowOrganize");
        };
        return menu;
    }

    private void RebuildRecentFiles()
    {
        var files = _settings.RecentFiles;

        if (_recentMenu != null)
        {
            _recentMenu.Items.Clear();
            foreach (var path in files)
                _recentMenu.Add(new NativeMenuItem { Header = Path.GetFileName(path), Command = _viewModel.OpenRecentCommand, CommandParameter = path, ToolTip = path });
            if (files.Count > 0)
                _recentMenu.Add(new NativeMenuItemSeparator());
            _recentMenu.Add(new NativeMenuItem { Header = Loc.T("MenuClearRecent"), Command = _viewModel.ClearRecentCommand });
        }

        var flyout = new MenuFlyout();
        foreach (var path in files)
            flyout.Items.Add(new MenuItem { Header = Path.GetFileName(path), Command = _viewModel.OpenRecentCommand, CommandParameter = path });
        if (files.Count == 0)
            flyout.Items.Add(new MenuItem { Header = Loc.T("RecentEmpty"), IsEnabled = false });
        else
        {
            flyout.Items.Add(new Separator());
            flyout.Items.Add(new MenuItem { Header = Loc.T("MenuClearRecent"), Command = _viewModel.ClearRecentCommand });
        }
        RecentButton.Flyout = flyout;

        StartRecent.Children.Clear();
        foreach (var path in files.Take(5))
            StartRecent.Children.Add(new Button
            {
                Content = Path.GetFileName(path),
                Command = _viewModel.OpenRecentCommand,
                CommandParameter = path,
                Background = Avalonia.Media.Brushes.Transparent,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            });
    }

    /// <summary>Called after a language change: rebuild everything that is not bound live (in all windows).</summary>
    private void OnSettingsChanged()
    {
        foreach (var window in AllViews)
        {
            window._viewModel.RefreshTexts();
            window.BuildMenuAndShortcuts();
        }
        try
        {
            _settings.Save();
        }
        catch (Exception)
        {
            // Not being able to save settings must not break the dialog
        }
    }

    #endregion

    #region Drop

    /// <summary>Dropped files: the first one opens here (if the window is empty), each further one in its own window.</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled || IsDialogOpen)
            return; // dropped into a dialog (web version: dialogs are part of the view)
        var files = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().ToList() ?? [];
        foreach (var file in _windows != null ? files : files.Take(1))
            if (await PlatformFeatures.Files.ToOpenPathAsync(file) is { } path)
                await _viewModel.OpenPathAsync(path);
    }

    #endregion

    #region IDialogService

    /// <summary>
    /// Shows a dialog: as a window owned by this view's window (desktop) or as an overlay on the page (web version). Only
    /// the one that is shown is created (<paramref name="window"/> or <paramref name="view"/>).
    /// </summary>
    private async Task<T?> ShowDialogAsync<T>(Func<Window> window, Func<DialogView> view)
    {
        if (OwnerWindow is { } owner)
            return await window().ShowDialog<T?>(owner);
        var result = await Dialogs.ShowAsync(view());
        return result is T value ? value : default;
    }

    /// <summary>A dialog overlay is open (web version): the page's shortcuts must not act on the document behind it.</summary>
    private bool IsDialogOpen => Dialogs.HasOpenDialog;

    public bool IsAvailable(ICommand? command) => PlatformFeatures.Current.IsAvailable(_viewModel, command);

    public bool IsButtonAvailable(string? name) => PlatformFeatures.Current.IsButtonAvailable(name);

    private static FilePickerFileType PdfFilter() => new(Loc.T("FilterPdf"))
    {
        Patterns = ["*.pdf"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf"],
        MimeTypes = ["application/pdf"],
    };

    public async Task<string?> PickPdfFileAsync()
    {
        var files = await Host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("PickPdfTitle"),
            AllowMultiple = false,
            FileTypeFilter = [PdfFilter()],
        });
        return files.Count > 0 ? await PlatformFeatures.Files.ToOpenPathAsync(files[0]) : null;
    }

    public async Task<IReadOnlyList<string>> PickPdfFilesAsync(string title)
    {
        var files = await Host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [PdfFilter()],
        });
        var paths = new List<string>();
        foreach (var file in files)
            if (await PlatformFeatures.Files.ToOpenPathAsync(file) is { } path)
                paths.Add(path);
        return paths;
    }

    public async Task<string?> PickSavePathAsync(string suggestedFileName, string? initialFolder)
    {
        var file = await Host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("PickSaveTitle"),
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "pdf",
            FileTypeChoices = [PdfFilter()],
            SuggestedStartLocation = initialFolder != null && !PlatformFeatures.Current.IsWeb ? await Host.StorageProvider.TryGetFolderFromPathAsync(initialFolder) : null,
            ShowOverwritePrompt = true,
        });
        return file != null ? PlatformFeatures.Files.ToSavePath(file) : null;
    }

    public async Task<string?> PickSaveFilePathAsync(string suggestedFileName, string? initialFolder)
    {
        var extension = Path.GetExtension(suggestedFileName).TrimStart('.');
        var file = await Host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("PickSaveTitle"),
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension.Length > 0 ? extension : null,
            FileTypeChoices = extension.Length > 0
                ? [new FilePickerFileType(Loc.F("FilterExtension", extension.ToUpperInvariant())) { Patterns = [$"*.{extension}"] }, FilePickerFileTypes.All]
                : [FilePickerFileTypes.All],
            SuggestedStartLocation = initialFolder != null && !PlatformFeatures.Current.IsWeb ? await Host.StorageProvider.TryGetFolderFromPathAsync(initialFolder) : null,
            ShowOverwritePrompt = true,
        });
        return file != null ? PlatformFeatures.Files.ToSavePath(file) : null;
    }

    public async Task<bool> OpenInSystemAsync(string path)
    {
        try
        {
            return await Host.Launcher.LaunchFileInfoAsync(new FileInfo(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public Task<AttachmentChoice?> ShowAttachmentsAsync(IReadOnlyList<PdfAttachment> attachments) =>
        ShowDialogAsync<AttachmentChoice?>(() => new AttachmentsDialog(attachments), () => new AttachmentsDialogView(attachments));

    public Task<RotateResult?> ShowRotateDialogAsync(RotateDialogContext context) =>
        ShowDialogAsync<RotateResult?>(() => new RotateDialog(context), () => new RotateDialogView(context));

    public Task<IReadOnlyList<int>?> ShowInsertRangeDialogAsync(InsertRangeContext context) =>
        ShowDialogAsync<IReadOnlyList<int>?>(() => new InsertRangeDialog(context), () => new InsertRangeDialogView(context));

    public Task<ReplacePagesResult?> ShowReplacePagesDialogAsync(ReplacePagesContext context) =>
        ShowDialogAsync<ReplacePagesResult?>(() => new ReplacePagesDialog(context), () => new ReplacePagesDialogView(context));

    public Task<PrintRequest?> ShowPrintDialogAsync(PrintDialogContext context) =>
        new PrintDialog(context).ShowDialog<PrintRequest?>(RequireWindow());

    public Task<SplitRequest?> ShowSplitDialogAsync(SplitDialogContext context) =>
        ShowDialogAsync<SplitRequest?>(() => new SplitDialog(context), () => new SplitDialogView(context));

    public Task<OcrRequest?> ShowOcrDialogAsync(OcrDialogContext context) =>
        ShowDialogAsync<OcrRequest?>(() => new OcrDialog(context), () => new OcrDialogView(context));

    public Task<ConcatenateResult?> ShowConcatenateDialogAsync(ConcatenateContext context) =>
        ShowDialogAsync<ConcatenateResult?>(() => new ConcatenateDialog(context, _viewModel.Document?.PageCount ?? 0),
            () => new ConcatenateDialogView(context, _viewModel.Document?.PageCount ?? 0));

    public Task<UnsavedChoice> AskUnsavedChangesAsync(string fileName) =>
        ShowDialogAsync<UnsavedChoice>(() => new UnsavedChangesDialog(fileName), () => new UnsavedChangesDialogView(fileName));

    public async Task<RedactedSaveAnswer> AskOverwriteRedactedAsync(string fileName, int pageCount) =>
        await new RedactedSaveDialog(fileName, pageCount).ShowDialog<RedactedSaveAnswer?>(RequireWindow())
        ?? new RedactedSaveAnswer(RedactedSaveChoice.Cancel, false); // closed with [X]

    public Task ShowAboutAsync() => ShowDialogAsync<object?>(() => new AboutWindow(), () => new AboutView());

    private QuickGuideWindow? _guide;

    /// <summary>Not modal, so the guide can stay open beside the document; a second call brings it to the front.</summary>
    public Task ShowQuickGuideAsync()
    {
        if (OwnerWindow == null)
            return Dialogs.ShowAsync(new QuickGuideView()); // web version: an overlay
        if (_guide != null)
            _guide.Activate();
        else
        {
            _guide = new QuickGuideWindow();
            _guide.Closed += (_, _) => _guide = null;
            _guide.Show(RequireWindow());
        }
        return Task.CompletedTask;
    }

    public async Task ShowIntroVideoAsync()
    {
        if (IntroVideo.Web is { } web)
        {
            if (web.Pick(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) is { } language)
                await web.Play(language);
            return;
        }
        if (IntroVideo.Current is not { } video)
            return;
        bool played;
        try
        {
            played = await Host.Launcher.LaunchFileInfoAsync(new FileInfo(video));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            played = false;
        }
        if (!played)
            await new MessageDialog(Loc.T("VideoOpenFailed"), Loc.T("VideoNoPlayer")).ShowDialog(RequireWindow());
    }

    public Task ShowSettingsAsync() =>
        ShowDialogAsync<object?>(() => new SettingsWindow(_settings, OnSettingsChanged), () => new SettingsView(_settings, OnSettingsChanged));

    public Task ShowErrorAsync(string message, string? details = null) =>
        ShowDialogAsync<object?>(() => new MessageDialog(message, details), () => new MessageDialogView(message, details));

    public Task ShowInfoAsync(string message, string? details = null) =>
        ShowDialogAsync<object?>(() => new MessageDialog(message, details, Loc.T("AppName")),
            () => new MessageDialogView(message, details, Loc.T("AppName")));

    public Task<ImageReduction?> ShowReduceSizeDialogAsync(ImageReduction last) =>
        ShowDialogAsync<ImageReduction?>(() => new ReduceSizeDialog(last), () => new ReduceSizeDialogView(last));

    /// <summary>Line breaks as the platform expects them (CRLF on Windows, e.g. for Notepad and Office).</summary>
    public async Task CopyToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetValueAsync(DataFormat.Text, text.ReplaceLineEndings());
    }

    public void CloseWindow() => CloseRequested?.Invoke();

    #endregion
}
