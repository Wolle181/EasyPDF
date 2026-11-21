using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using EasyPDF.App.Controls;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Stamps;
using EasyPDF.Core.Templates;

namespace EasyPDF.App.Dialogs;

/// <summary>What the stamp editor made: the name the user gave it and the finished PNG file.</summary>
public sealed record StampEditorResult(string Name, byte[] Png);

/// <summary>
/// Makes a stamp from a PNG picture or an empty area: fields are drawn on it and show the result with today's date at once.
/// Stamps are PNG only (<see cref="StampLibrary"/>). The result is the finished stamp, or null if
/// cancelled (also when closed with [X]); nothing is written here.
/// </summary>
public sealed class StampEditorView : DialogView
{
    // An empty area: 60 x 25 mm at 300 dpi, sharp in print like the sample stamps
    private const int BlankWidth = 709;
    private const int BlankHeight = 295;
    private const double BlankDpi = 300;
    private const double DefaultDpi = 96;
    private const string NewFieldText = "{$DATE}";
    private const string NewFieldColor = "#C00000";

    private readonly TemplateContext _context;
    private readonly List<StampField> _fields = [];
    private readonly StampEditorCanvas _canvas = new() { Width = 460, Height = 300 };
    private readonly TextBox _name = new() { Width = 220 };
    private readonly CheckBox _white = new() { Content = Loc.T("StampEditorWhite") };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBox _text = new();
    private readonly TextBox _color = new() { Width = 100, MaxLength = 7, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _upper = new() { Content = Loc.T("StampFieldUpper") };
    private readonly ComboBox _align = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        ItemsSource = new[] { Loc.T("StampFieldAlignCenter"), Loc.T("StampFieldAlignLeft"), Loc.T("StampFieldAlignRight") },
    };
    private readonly ComboBox _language = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        ItemsSource = new[] { Loc.T("StampFieldLanguageProgram"), "Deutsch", "English" },
    };
    private readonly StackPanel _fieldPanel = new() { Spacing = 6, IsEnabled = false };
    private readonly Button _ok = new() { Content = Loc.T("Ok"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };

    private static readonly string?[] Languages = [null, "de", "en"];

    private byte[] _picture;
    private byte[] _base;
    private double _dpi;
    private int _selected = -1;
    private bool _loading;
    private bool _nameTyped;
    private Bitmap? _preview;

    public StampEditorView() : this(null, new TemplateContext(DateTime.Now, "", "", 1, 1))
    {
    }

    /// <param name="path">The PNG file to edit; null starts with an empty area. Throws if it isn't a PNG that can be read.</param>
    /// <param name="context">What macros stand for in the preview.</param>
    public StampEditorView(string? path, TemplateContext context)
    {
        _context = context;
        Title = Loc.T("StampEditorTitle");
        DialogWidth = 780;

        if (path != null)
        {
            var file = File.ReadAllBytes(path);
            RequirePng(file);
            _picture = file;
            _dpi = StampLibrary.ReadDpi(file) ?? DefaultDpi;
            _fields.AddRange(StampFile.ReadDefinition(file)?.Fields ?? []);
            _name.Text = Path.GetFileNameWithoutExtension(path);
            _nameTyped = true; // a picture chosen later doesn't rename a stamp that has a name
        }
        else
        {
            _picture = BlankPicture();
            _dpi = BlankDpi;
            _name.Text = Loc.T("StampDefaultName");
        }
        _base = _picture;

        Content = BuildContent();
        WireEvents();
        UpdatePreview();
        Select(_fields.Count > 0 ? 0 : -1);
        Closed += (_, _) => _preview?.Dispose();
    }

    /// <summary>The canvas with the picture and fields.</summary>
    public StampEditorCanvas Canvas => _canvas;

    /// <summary>The fields as they are at the moment.</summary>
    public IReadOnlyList<StampField> Fields => _fields;

    private StackPanel BuildContent()
    {
        var choose = new Button { Content = Loc.T("StampEditorChoosePicture") };
        choose.Click += async (_, _) => await ChoosePictureAsync();
        var cancel = new Button { Content = Loc.T("Cancel"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        cancel.Click += (_, _) => Close((StampEditorResult?)null);
        _ok.Click += (_, _) => Close(BuildResult());

        var remove = new Button { Content = Loc.T("StampFieldRemove") };
        remove.Click += (_, _) => RemoveSelected();

        var swatches = new WrapPanel { MaxWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var hex in TextStyle.Palette)
        {
            var swatch = new Button
            {
                Width = 26,
                Height = 22,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse(hex)),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) =>
            {
                _color.Text = hex;
                ChangeSelected(f => f with { Color = hex });
            };
            swatches.Children.Add(swatch);
        }

        _fieldPanel.Children.Add(new TextBlock { Text = Loc.T("StampFieldText") });
        _fieldPanel.Children.Add(_text);
        _fieldPanel.Children.Add(new TextBlock { Text = Loc.T("TemplateMacros"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 });
        _fieldPanel.Children.Add(new TextBlock { Text = Loc.T("StampFieldColor") });
        _fieldPanel.Children.Add(swatches);
        _fieldPanel.Children.Add(_color);
        _fieldPanel.Children.Add(new TextBlock { Text = Loc.T("StampFieldAlign") });
        _fieldPanel.Children.Add(_align);
        _fieldPanel.Children.Add(_upper);
        _fieldPanel.Children.Add(new TextBlock { Text = Loc.T("StampFieldLanguage") });
        _fieldPanel.Children.Add(_language);
        _fieldPanel.Children.Add(remove);

        return new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = Loc.T("StampEditorName"), VerticalAlignment = VerticalAlignment.Center },
                        _name,
                        choose,
                        _white,
                    },
                },
                new TextBlock { Text = Loc.T("StampEditorHint") + " " + Loc.T("StampPngHint"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children = { _canvas, new Border { Width = 260, Child = _fieldPanel } },
                },
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { _ok, cancel } },
            },
        };
    }

    private void WireEvents()
    {
        _canvas.SelectionRequested += Select;
        _canvas.DeleteRequested += RemoveSelected;
        _canvas.FieldDrawn += r =>
        {
            _fields.Add(new StampField { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Text = NewFieldText, Color = NewFieldColor });
            UpdatePreview();
            Select(_fields.Count - 1);
            _text.Focus();
            _text.SelectAll();
        };
        _canvas.FieldChanged += (i, r) =>
        {
            if (i < _fields.Count)
            {
                _fields[i] = _fields[i] with { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
                UpdatePreview();
            }
        };

        _name.TextChanged += (_, _) =>
        {
            _nameTyped = true;
            _ok.IsEnabled = !string.IsNullOrWhiteSpace(_name.Text);
        };
        _white.IsCheckedChanged += (_, _) => UpdateBase();
        _text.TextChanged += (_, _) => ChangeSelected(f => f with { Text = _text.Text ?? "" });
        _upper.IsCheckedChanged += (_, _) => ChangeSelected(f => f with { Uppercase = _upper.IsChecked == true });
        _align.SelectionChanged += (_, _) => ChangeSelected(f => f with { Align = (StampFieldAlign)Math.Max(0, _align.SelectedIndex) });
        _language.SelectionChanged += (_, _) => ChangeSelected(f => f with { Language = Languages[Math.Max(0, _language.SelectedIndex)] });
        _color.LostFocus += (_, _) => ApplyColorBox();
        _color.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                ApplyColorBox();
                e.Handled = true; // the box is confirmed, not the whole dialog
            }
        };
    }

    private void ApplyColorBox()
    {
        if (TextStyle.IsValidColor(_color.Text))
            ChangeSelected(f => f with { Color = TextStyle.NormalizeColor(_color.Text) });
        if (_selected >= 0)
            _color.Text = _fields[_selected].Color;
    }

    /// <summary>Selects a field and shows its values; -1 = none.</summary>
    private void Select(int index)
    {
        _selected = index;
        _canvas.SelectedIndex = index;
        _loading = true;
        var field = index >= 0 ? _fields[index] : null;
        _text.Text = field?.Text ?? "";
        _color.Text = field?.Color ?? "";
        _upper.IsChecked = field?.Uppercase == true;
        _align.SelectedIndex = (int)(field?.Align ?? StampFieldAlign.Center);
        _language.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, field?.Language));
        _fieldPanel.IsEnabled = field != null;
        _loading = false;
    }

    private void ChangeSelected(Func<StampField, StampField> change)
    {
        if (_loading || _selected < 0)
            return;
        var changed = change(_fields[_selected]);
        if (changed == _fields[_selected])
            return;
        _fields[_selected] = changed;
        UpdatePreview();
    }

    private void RemoveSelected()
    {
        if (_selected < 0)
            return;
        _fields.RemoveAt(_selected);
        UpdatePreview();
        Select(-1);
    }

    private async System.Threading.Tasks.Task ChoosePictureAsync()
    {
        if (Host is not { } host)
            return;
        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("StampEditorChoosePicture"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc.T("StampPictures")) { Patterns = ["*.png"], MimeTypes = ["image/png"] }],
        });
        if (files.Count == 0)
            return;
        if (files[0].TryGetLocalPath() is { } path)
        {
            UsePicture(path);
            return;
        }
        // The browser has no paths: the picture comes as a stream
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            UsePicture(memory.ToArray(), files[0].Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(Loc.F("StampUnreadable", files[0].Name));
        }
    }

    /// <summary>Takes the PNG in the file; the fields stay, unless there are none yet and the file brings its own. Any other file is refused with a message.</summary>
    public void UsePicture(string path)
    {
        byte[] file;
        try
        {
            file = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(Loc.F("StampUnreadable", Path.GetFileName(path)));
            return;
        }
        UsePicture(file, Path.GetFileName(path));
    }

    /// <summary>Takes the PNG <paramref name="file"/> named <paramref name="fileName"/>, as <see cref="UsePicture(string)"/>.</summary>
    public void UsePicture(byte[] file, string fileName)
    {
        var path = fileName;
        try
        {
            if (!StampFile.IsPng(file))
            {
                ShowError(Loc.F("StampEditorPngOnly", Path.GetFileName(path)));
                return;
            }
            RequirePng(file);
        }
        catch (Exception)
        {
            // Any unreadable file: the decoder can fail in many ways on damaged pictures
            ShowError(Loc.F("StampUnreadable", Path.GetFileName(path)));
            return;
        }
        _error.IsVisible = false;
        _picture = file;
        _dpi = StampLibrary.ReadDpi(file) ?? DefaultDpi;
        if (_fields.Count == 0 && StampFile.ReadDefinition(file) is { } definition)
            _fields.AddRange(definition.Fields);
        if (!_nameTyped || _name.Text == Loc.T("StampDefaultName"))
        {
            _name.Text = Path.GetFileNameWithoutExtension(path);
            _nameTyped = false;
        }
        UpdateBase();
        Select(_fields.Count > 0 ? 0 : -1);
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    /// <summary>The picture the fields are written on, after the white-to-transparent option.</summary>
    private void UpdateBase()
    {
        _base = _white.IsChecked == true ? WhiteToTransparent(_picture) : _picture;
        UpdatePreview();
    }

    private StampDefinition Definition => new() { Fields = [.. _fields.Where(f => !string.IsNullOrWhiteSpace(f.Text))] };

    private void UpdatePreview()
    {
        var old = _preview;
        using var stream = new MemoryStream(StampComposer.Compose(_base, Definition, _context).Png);
        _preview = new Bitmap(stream);
        _canvas.Picture = _preview;
        _canvas.Fields = [.. _fields.Select(f => new Rect(f.X, f.Y, f.Width, f.Height))];
        old?.Dispose();
    }

    private StampEditorResult BuildResult() =>
        new((_name.Text ?? "").Trim(), StampFile.WriteDpi(StampFile.WriteDefinition(_base, Definition), _dpi));

    /// <summary>Throws unless the file is a PNG the decoder can read.</summary>
    private static void RequirePng(byte[] file)
    {
        if (!StampFile.IsPng(file))
            throw new InvalidDataException("Not a PNG file.");
        using var stream = new MemoryStream(file);
        using var bitmap = new Bitmap(stream);
    }

    private static byte[] BlankPicture()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(BlankWidth, BlankHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var png = new MemoryStream();
        bitmap.Save(png, new PngBitmapEncoderOptions());
        return png.ToArray();
    }

    private static byte[] WhiteToTransparent(byte[] picture)
    {
        using var stream = new MemoryStream(picture);
        using var bitmap = new Bitmap(stream);
        var pixels = StampImage.WhiteToTransparent(StampPixels.ToStraightBgra(bitmap));
        var size = bitmap.PixelSize;
        using var result = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var fb = result.Lock())
            for (var y = 0; y < size.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * size.Width * 4, fb.Address + y * fb.RowBytes, size.Width * 4);
        using var png = new MemoryStream();
        result.Save(png, new PngBitmapEncoderOptions());
        return png.ToArray();
    }
}

/// <summary>The stamp editor as a window (desktop).</summary>
public sealed class StampEditorDialog : DialogWindow
{
    /// <inheritdoc cref="StampEditorView(string?, TemplateContext)"/>
    public StampEditorDialog(string? path, TemplateContext context) : base(new StampEditorView(path, context))
    {
    }

    private StampEditorView View => (StampEditorView)DialogContent;

    /// <inheritdoc cref="StampEditorView.Canvas"/>
    public StampEditorCanvas Canvas => View.Canvas;

    /// <inheritdoc cref="StampEditorView.Fields"/>
    public IReadOnlyList<StampField> Fields => View.Fields;

    /// <inheritdoc cref="StampEditorView.UsePicture(string)"/>
    public void UsePicture(string path) => View.UsePicture(path);
}
