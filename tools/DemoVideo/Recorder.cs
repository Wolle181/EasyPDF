using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace EasyPDF.DemoVideo;

/// <summary>
/// Turns a scripted session into a video: every output frame moves the simulated pointer, renders the real window headless,
/// draws dialogs, pointer and banner over it and pipes the result to ffmpeg. The video runs on its own clock (frames / fps), so
/// slow rendering makes the recording take longer but not the video jerk.
/// </summary>
internal sealed class Recorder : IDisposable
{
    public const int Width = 1920, Height = 1080, AppHeight = 900;
    public const int Fps = 30;
    public const double Scale = 1.25;

    private const float BannerFadeSeconds = 0.5f;

    /// <summary>Share of the scripted pauses that gets into the video: below 1 tightens the pace without hurrying the pointer.</summary>
    public const double PauseFactor = 0.7;

    /// <summary>How long the mouse with the pressed right button shows next to the pointer after a right click (s).</summary>
    private const double RightClickHintSeconds = 1.2;

    // The brand colours of the app being recorded (brand profile)
    private static readonly SKColor Primary = ToSk(EasyPDF.App.Styles.BrandColors.Primary);
    private static readonly SKColor Accent = ToSk(EasyPDF.App.Styles.BrandColors.Accent);

    private static SKColor ToSk(Avalonia.Media.Color c) => new(c.R, c.G, c.B, c.A);

    private readonly Process _ffmpeg;
    private readonly Stream _output;
    private readonly SKBitmap _target = new(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
    private readonly SKCanvas _canvas;
    private readonly SKTypeface _regular, _semiBold, _bold;
    private readonly SKBitmap _logo;
    private readonly Stopwatch _wall = Stopwatch.StartNew();

    private (string Title, string Text)? _banner, _oldBanner;
    private int _bannerAge = int.MaxValue;
    private (double X, double Y) _pointer = (Width * 0.7, AppHeight * 0.75);
    private bool _pointerShown = true;
    private double _ripple = -1;
    private double _rightClickHint = -1; // seconds since the last right click, -1 = none
    private Action<SKCanvas, float>? _card;
    private float _cardOpacity;

    public Window? Window { get; set; }

    /// <summary>Runs before each frame is drawn.</summary>
    public Action? EachFrame { get; set; }

    public long Frames { get; private set; }

    /// <summary>The time of the video in ms: the clock the reading view glides on.</summary>
    public long VideoMs => Frames * 1000 / Fps;

    public Recorder(string ffmpeg, string outputFile, string fontFolder, string logoFile)
    {
        _canvas = new SKCanvas(_target);
        _regular = SKTypeface.FromFile(Path.Combine(fontFolder, "Barlow-Regular.ttf"));
        _semiBold = SKTypeface.FromFile(Path.Combine(fontFolder, "Barlow-SemiBold.ttf"));
        _bold = SKTypeface.FromFile(Path.Combine(fontFolder, "Barlow-Bold.ttf"));
        _logo = SKBitmap.Decode(logoFile);

        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-y", "-hide_banner", "-loglevel", "error",
                     "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{Width}x{Height}", "-r", Fps.ToString(), "-i", "-",
                     "-an", "-c:v", "libx264", "-preset", "slow", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", outputFile,
                 })
            info.ArgumentList.Add(argument);
        _ffmpeg = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start.");
        _output = _ffmpeg.StandardInput.BaseStream;
    }

    public void Banner(string title, string text)
    {
        Console.WriteLine($"{VideoMs / 1000.0,6:F1} s  {title}");
        _oldBanner = _banner;
        _banner = (title, text);
        _bannerAge = 0;
        using var font = new SKFont(_semiBold, 58);
        using var small = new SKFont(_regular, 38);
        if (font.MeasureText(title) > Width - 120 || small.MeasureText(text) > Width - 120)
            throw new InvalidOperationException($"Banner too long: {title} / {text}");
    }

    public void ShowPointer(bool shown) => _pointerShown = shown;

    #region Time

    /// <summary>Runs the app without adding to the video (loading behind a card or before a cut).</summary>
    public static async Task Settle(int ms)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    /// <summary>One frame of the video.</summary>
    public async Task Frame()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(1);
        Dispatcher.UIThread.RunJobs();
        EachFrame?.Invoke();
        Compose();
        unsafe
        {
            _output.Write(new ReadOnlySpan<byte>((void*)_target.GetPixels(), _target.ByteCount));
        }
        Frames++;
        if (_bannerAge != int.MaxValue)
            _bannerAge++;
        if (_ripple >= 0)
            _ripple = _ripple >= 0.5 ? -1 : _ripple + 1.0 / Fps;
        if (_rightClickHint >= 0)
            _rightClickHint = _rightClickHint >= RightClickHintSeconds ? -1 : _rightClickHint + 1.0 / Fps;
    }

    public async Task Wait(double seconds)
    {
        for (var i = 0; i < seconds * Fps; i++)
            await Frame();
    }

    /// <summary>A pause of the storyboard (looking at the result of a step), shortened by <see cref="PauseFactor"/>.</summary>
    public Task Pause(double seconds) => Wait(seconds * PauseFactor);

    /// <summary>
    /// Records until the condition holds, for at most <paramref name="timeoutSeconds"/> of real time. With
    /// <paramref name="maxVideoSeconds"/> only that much of the wait gets into the video.
    /// </summary>
    public async Task WaitUntil(Func<bool> condition, double timeoutSeconds = 60, double maxVideoSeconds = double.MaxValue)
    {
        var start = _wall.Elapsed.TotalSeconds;
        var recorded = 0;
        while (!condition())
        {
            if (_wall.Elapsed.TotalSeconds - start > timeoutSeconds)
                throw new TimeoutException("A scripted step did not finish in time.");
            if (recorded++ < maxVideoSeconds * Fps)
                await Frame();
            else
                await Settle(40);
        }
    }

    public async Task Card(Action<SKCanvas, float> draw, double seconds, bool fadeIn, bool fadeOut)
    {
        const double fade = 0.5;
        _card = draw;
        var total = (int)(seconds * Fps);
        for (var i = 0; i < total; i++)
        {
            var t = i / (double)Fps;
            var left = (total - i) / (double)Fps;
            _cardOpacity = (float)Math.Min(fadeIn ? Math.Min(1, t / fade) : 1, fadeOut ? Math.Min(1, left / fade) : 1);
            await Frame();
        }
        _card = null;
    }

    #endregion

    #region Pointer

    /// <summary>The window that gets the input: the dialog on top, if one is open.</summary>
    public Window TopWindow
    {
        get
        {
            var window = Window!;
            while (window.OwnedWindows.LastOrDefault(w => w.IsVisible) is { } owned)
                window = owned;
            return window;
        }
    }

    private (double X, double Y) WindowOffset(Window window)
    {
        if (ReferenceEquals(window, Window))
            return (0, 0);
        var (width, height) = (window.Bounds.Width * Scale, window.Bounds.Height * Scale);
        return ((Width - width) / 2, (AppHeight - height + TitleBarHeight) / 2);
    }

    private (double X, double Y) ToVideo(Window window, Point p)
    {
        var offset = WindowOffset(window);
        return (offset.X + p.X * Scale, offset.Y + p.Y * Scale);
    }

    private Point ToWindow(Window window, (double X, double Y) p)
    {
        var offset = WindowOffset(window);
        return new Point((p.X - offset.X) / Scale, (p.Y - offset.Y) / Scale);
    }

    /// <summary>A point of a control (window coordinates; default: its centre).</summary>
    public static Point PointIn(Control control, double fractionX = 0.5, double fractionY = 0.5)
    {
        var window = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("Control is not shown.");
        return control.TranslatePoint(new Point(control.Bounds.Width * fractionX, control.Bounds.Height * fractionY), window)
            ?? throw new InvalidOperationException("Control has no position.");
    }

    /// <summary>Glides to a point of the top window, sending the moves; the modifiers tell whether a button is held.</summary>
    public async Task MoveTo(Point target, double seconds = 0.6, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var window = TopWindow;
        var from = _pointer;
        var to = ToVideo(window, target);
        var steps = Math.Max(1, (int)(seconds * Fps));
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            var eased = t * t * (3 - 2 * t);
            _pointer = (from.X + (to.X - from.X) * eased, from.Y + (to.Y - from.Y) * eased);
            window.MouseMove(ToWindow(window, _pointer), modifiers);
            await Frame();
        }
    }

    public async Task Click(Point target, double seconds = 0.6, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        await MoveTo(target, seconds, modifiers);
        await Wait(0.12);
        var window = TopWindow;
        var at = ToWindow(window, _pointer);
        window.MouseDown(at, button, modifiers);
        _ripple = 0;
        if (button == MouseButton.Right)
            _rightClickHint = 0;
        await Frame();
        window.MouseUp(at, button, modifiers);
        await Frame();
    }

    public Task Click(Control control, double seconds = 0.6) => Click(PointIn(control), seconds);

    /// <summary>A right click: the video shows a mouse with its right button pressed next to the pointer.</summary>
    public Task RightClick(Point target, double seconds = 0.6) => Click(target, seconds, MouseButton.Right);

    /// <summary>Presses at <paramref name="from"/>, drags to <paramref name="to"/> and releases.</summary>
    public async Task Drag(Point from, Point to, double seconds = 0.9, bool fling = false)
    {
        await MoveTo(from);
        await Wait(0.15);
        var window = TopWindow;
        window.MouseDown(ToWindow(window, _pointer), MouseButton.Left);
        _ripple = 0;
        await Frame();
        await MoveTo(to, seconds, RawInputModifiers.LeftMouseButton);
        if (!fling)
            await Wait(0.2);
        window.MouseUp(ToWindow(window, _pointer), MouseButton.Left);
        await Frame();
    }

    public async Task Wheel(double delta, int steps, double secondsPerStep = 0.22)
    {
        var window = TopWindow;
        for (var i = 0; i < steps; i++)
        {
            window.MouseWheel(ToWindow(window, _pointer), new Vector(0, delta), RawInputModifiers.Control);
            await Wait(secondsPerStep);
        }
    }

    public async Task Key(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var window = TopWindow;
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        await Frame();
    }

    public async Task Type(string text, double secondsPerKey = 0.055)
    {
        var window = TopWindow;
        foreach (var c in text)
        {
            window.KeyTextInput(c.ToString());
            await Wait(secondsPerKey);
        }
    }

    #endregion

    #region Drawing

    private const int TitleBarHeight = 44;

    private void Compose()
    {
        var canvas = _canvas;
        canvas.Clear(Primary);
        if (Window is { IsVisible: true })
        {
            DrawWindow(canvas, Window, 0, 0);
            foreach (var dialog in OpenDialogs())
                DrawDialog(canvas, dialog);
        }
        DrawBanner(canvas);
        if (Window is { IsVisible: true })
            DrawPointer(canvas);
        if (_card != null && _cardOpacity > 0)
        {
            canvas.SaveLayer(new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * _cardOpacity)) });
            _card(canvas, _cardOpacity);
            canvas.Restore();
        }
        canvas.Flush();
    }

    private IEnumerable<Window> OpenDialogs()
    {
        var window = Window!;
        while (window.OwnedWindows.LastOrDefault(w => w.IsVisible) is { } owned)
        {
            yield return owned;
            window = owned;
        }
    }

    private static void DrawWindow(SKCanvas canvas, Window window, float x, float y)
    {
        window.SetRenderScaling(Scale);
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
        using var buffer = frame.Lock();
        var type = buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
        using var bitmap = new SKBitmap();
        bitmap.InstallPixels(new SKImageInfo(buffer.Size.Width, buffer.Size.Height, type, SKAlphaType.Premul), buffer.Address, buffer.RowBytes);
        canvas.DrawBitmap(bitmap, x, y);
    }

    /// <summary>A dialog over the dimmed main window, with the title bar the system would give it.</summary>
    private void DrawDialog(SKCanvas canvas, Window dialog)
    {
        var (x, y) = WindowOffset(dialog);
        var (width, height) = ((float)(dialog.Bounds.Width * Scale), (float)(dialog.Bounds.Height * Scale));
        var rect = new SKRect((float)x, (float)y - TitleBarHeight, (float)x + width, (float)y + height);
        using (var dim = new SKPaint { Color = SKColors.Black.WithAlpha(90) })
            canvas.DrawRect(0, 0, Width, AppHeight, dim);
        using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(110), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 22), IsAntialias = true })
            canvas.DrawRect(rect, shadow);
        using (var bar = new SKPaint { Color = Primary, IsAntialias = true })
            canvas.DrawRect(rect.Left, rect.Top, width, TitleBarHeight, bar);
        using (var font = new SKFont(_semiBold, 24))
        using (var ink = new SKPaint { Color = SKColors.White, IsAntialias = true })
            canvas.DrawText(dialog.Title ?? "", rect.Left + 16, rect.Top + 30, font, ink);
        DrawWindow(canvas, dialog, (float)x, (float)y);
    }

    private void DrawPointer(SKCanvas canvas)
    {
        if (_ripple >= 0)
        {
            var t = (float)(_ripple / 0.5);
            using var ring = new SKPaint { Color = Accent.WithAlpha((byte)(230 * (1 - t))), Style = SKPaintStyle.Stroke, StrokeWidth = 6, IsAntialias = true };
            canvas.DrawCircle((float)_pointer.X, (float)_pointer.Y, 10 + 36 * t, ring);
        }
        if (_rightClickHint >= 0)
            DrawRightClickHint(canvas);
        if (!_pointerShown)
            return;
        var kind = PointerKind();
        if (kind == "None")
            return;
        var (x, y) = ((float)_pointer.X, (float)_pointer.Y);
        using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var edge = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
        if (kind == "Cross")
        {
            using var path = new SKPath();
            foreach (var (dx, dy) in new (float, float)[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                path.MoveTo(x + dx * 5, y + dy * 5);
                path.LineTo(x + dx * 20, y + dy * 20);
            }
            using var white = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 6, IsAntialias = true };
            using var black = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
            canvas.DrawPath(path, white);
            canvas.DrawPath(path, black);
            return;
        }
        if (kind == "Ibeam")
        {
            using var path = new SKPath();
            path.MoveTo(x, y - 16);
            path.LineTo(x, y + 16);
            path.MoveTo(x - 7, y - 18);
            path.LineTo(x + 7, y - 18);
            path.MoveTo(x - 7, y + 18);
            path.LineTo(x + 7, y + 18);
            using var white = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 6, IsAntialias = true };
            using var black = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
            canvas.DrawPath(path, white);
            canvas.DrawPath(path, black);
            return;
        }
        using var arrow = new SKPath();
        const float s = 1.7f;
        arrow.MoveTo(x, y);
        arrow.LineTo(x, y + 21 * s);
        arrow.LineTo(x + 5 * s, y + 16 * s);
        arrow.LineTo(x + 9 * s, y + 25 * s);
        arrow.LineTo(x + 12.5f * s, y + 23.5f * s);
        arrow.LineTo(x + 8.5f * s, y + 15 * s);
        arrow.LineTo(x + 15 * s, y + 15 * s);
        arrow.Close();
        canvas.DrawPath(arrow, fill);
        canvas.DrawPath(arrow, edge);
    }

    /// <summary>A small mouse beside the pointer with its right button filled in the accent colour: "this was a right click".</summary>
    private void DrawRightClickHint(SKCanvas canvas)
    {
        var fade = (float)Math.Min(1, (RightClickHintSeconds - _rightClickHint) / 0.3);
        var alpha = (byte)(255 * Math.Clamp(fade, 0, 1));
        float w = 44, h = 64;
        var left = (float)_pointer.X - w - 18;
        var top = (float)_pointer.Y + 6;
        var body = new SKRoundRect(new SKRect(left, top, left + w, top + h), w / 2, w / 2);
        using var white = new SKPaint { Color = SKColors.White.WithAlpha(alpha), IsAntialias = true };
        using var outline = new SKPaint { Color = Primary.WithAlpha(alpha), Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        using var accent = new SKPaint { Color = Accent.WithAlpha(alpha), IsAntialias = true };
        canvas.DrawRoundRect(body, white);
        // The right button: the top right quarter of the body
        canvas.Save();
        canvas.ClipRoundRect(body, antialias: true);
        canvas.DrawRect(left + w / 2, top, w / 2, h * 0.42f, accent);
        canvas.Restore();
        canvas.DrawRoundRect(body, outline);
        canvas.DrawLine(left + w / 2, top, left + w / 2, top + h * 0.42f, outline);
        canvas.DrawLine(left, top + h * 0.42f, left + w, top + h * 0.42f, outline);
    }

    /// <summary>The pointer the app asks for at the current position ("None" while a stamp preview replaces it).</summary>
    private string PointerKind()
    {
        var window = TopWindow;
        var hit = window.InputHitTest(ToWindow(window, _pointer));
        return (hit as InputElement)?.Cursor?.ToString() ?? "Arrow";
    }

    private void DrawBanner(SKCanvas canvas)
    {
        using (var band = new SKPaint { Color = Primary })
            canvas.DrawRect(0, AppHeight, Width, Height - AppHeight, band);
        using (var bar = new SKPaint { Color = Accent })
            canvas.DrawRect(0, AppHeight, 16, Height - AppHeight, bar);
        var seconds = _bannerAge == int.MaxValue ? BannerFadeSeconds : Math.Min(BannerFadeSeconds, _bannerAge / (float)Fps);
        var t = seconds / BannerFadeSeconds;
        // The old text leaves before the new one comes, so the two never overlap
        if (_oldBanner is { } old && t < 0.5f)
            DrawBannerText(canvas, old, 1 - t * 2, 0);
        if (_banner is { } banner && (t >= 0.5f || _oldBanner == null))
            DrawBannerText(canvas, banner, _oldBanner == null ? t : t * 2 - 1, (1 - t) * 36);
    }

    private void DrawBannerText(SKCanvas canvas, (string Title, string Text) banner, float opacity, float offset)
    {
        var alpha = (byte)(255 * opacity);
        using var titleFont = new SKFont(_semiBold, 58);
        using var textFont = new SKFont(_regular, 38);
        using var titleInk = new SKPaint { Color = Accent.WithAlpha(alpha), IsAntialias = true };
        using var textInk = new SKPaint { Color = SKColors.White.WithAlpha(alpha), IsAntialias = true };
        canvas.DrawText(banner.Title, 60, AppHeight + 78 + offset, titleFont, titleInk);
        canvas.DrawText(banner.Text, 60, AppHeight + 138 + offset, textFont, textInk);
    }

    /// <summary>A full-screen card: logo, product name, tagline and a line of facts.</summary>
    public Action<SKCanvas, float> TitleCard(string name, string tagline, string? facts = null) => (canvas, _) =>
    {
        canvas.Clear(Primary);
        var logoSize = 200f;
        using (var image = new SKPaint { IsAntialias = true })
            canvas.DrawBitmap(_logo, new SKRect((Width - logoSize) / 2, 210, (Width + logoSize) / 2, 210 + logoSize), image);
        using var nameFont = new SKFont(_bold, 128);
        using var taglineFont = new SKFont(_regular, 56);
        using var factsFont = new SKFont(_semiBold, 42);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var accent = new SKPaint { Color = Accent, IsAntialias = true };
        canvas.DrawText(name, Width / 2f, 590, SKTextAlign.Center, nameFont, white);
        canvas.DrawRect((Width - 140) / 2f, 630, 140, 8, accent);
        canvas.DrawText(tagline, Width / 2f, 720, SKTextAlign.Center, taglineFont, white);
        if (facts != null)
            canvas.DrawText(facts, Width / 2f, 800, SKTextAlign.Center, factsFont, accent);
    };

    #endregion

    public void Dispose()
    {
        _output.Dispose();
        _ffmpeg.WaitForExit();
        _ffmpeg.Dispose();
        _canvas.Dispose();
        _target.Dispose();
        _logo.Dispose();
    }
}
