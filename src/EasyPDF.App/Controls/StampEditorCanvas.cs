using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EasyPDF.App.Controls;

/// <summary>
/// The working area of the stamp editor: shows the picture with its fields. Dragging on free space draws a new field,
/// dragging a field moves it, a corner of the selected field resizes it. All rectangles are shares of the picture (0 to 1).
/// </summary>
public sealed class StampEditorCanvas : Control
{
    private const double Inset = 8;
    private const double HandleSize = 8;
    private const double MinFieldPixels = 8;

    private Bitmap? _picture;
    private IReadOnlyList<Rect> _fields = [];
    private int _selected = -1;

    private int _dragField = -1;
    private int _dragCorner = -1;
    private bool _drawing;
    private Point _dragStart;
    private Rect _dragStartRect;
    private Rect _dragCurrent;

    /// <summary>A field was clicked, or free space (-1).</summary>
    public event Action<int>? SelectionRequested;

    /// <summary>A new field was drawn.</summary>
    public event Action<Rect>? FieldDrawn;

    /// <summary>A field was moved or resized.</summary>
    public event Action<int, Rect>? FieldChanged;

    /// <summary>Delete was pressed with a field selected.</summary>
    public event Action? DeleteRequested;

    public StampEditorCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>The picture to show; stays with the caller, who disposes it.</summary>
    public Bitmap? Picture
    {
        get => _picture;
        set
        {
            _picture = value;
            InvalidateVisual();
        }
    }

    public IReadOnlyList<Rect> Fields
    {
        get => _fields;
        set
        {
            _fields = value;
            InvalidateVisual();
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            _selected = value;
            InvalidateVisual();
        }
    }

    /// <summary>Where the picture lies in the control: as large as fits, centred.</summary>
    public Rect PictureArea
    {
        get
        {
            var (width, height) = _picture is { } picture ? (picture.PixelSize.Width, picture.PixelSize.Height) : (1, 1);
            var room = new Size(Math.Max(1, Bounds.Width - 2 * Inset), Math.Max(1, Bounds.Height - 2 * Inset));
            var scale = Math.Min(room.Width / width, room.Height / height);
            var size = new Size(width * scale, height * scale);
            return new Rect(new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2), size);
        }
    }

    public override void Render(DrawingContext context)
    {
        var area = PictureArea;
        context.FillRectangle(Brushes.White, area);
        if (_picture is { } picture)
            context.DrawWholeBitmap(picture, area);
        context.DrawRectangle(new Pen(Brushes.Gray, 1), area);
        for (var i = 0; i < _fields.Count; i++)
            if (i != _dragField)
                DrawField(context, ToScreen(_fields[i]), i == _selected);
        if (_dragField >= 0 || _drawing)
            DrawField(context, ToScreen(_dragCurrent), true);
    }

    private static void DrawField(DrawingContext context, Rect rect, bool selected)
    {
        context.DrawRectangle(new Pen(Brushes.DodgerBlue, 1, selected ? null : DashStyle.Dash), rect);
        if (!selected)
            return;
        foreach (var corner in Corners(rect))
            context.DrawRectangle(Brushes.White, new Pen(Brushes.DodgerBlue, 1), new Rect(corner.X - HandleSize / 2, corner.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    private static Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];

    private Rect ToScreen(Rect share)
    {
        var a = PictureArea;
        return new Rect(a.X + share.X * a.Width, a.Y + share.Y * a.Height, share.Width * a.Width, share.Height * a.Height);
    }

    private Point ToShare(Point screen)
    {
        var a = PictureArea;
        return new Point(Math.Clamp((screen.X - a.X) / a.Width, 0, 1), Math.Clamp((screen.Y - a.Y) / a.Height, 0, 1));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Focus();
        var position = e.GetPosition(this);
        _dragStart = position;
        e.Pointer.Capture(this);
        e.Handled = true;

        if (_selected >= 0 && _selected < _fields.Count)
        {
            var corners = Corners(ToScreen(_fields[_selected]));
            for (var c = 0; c < corners.Length; c++)
            {
                if (Math.Abs(corners[c].X - position.X) > HandleSize || Math.Abs(corners[c].Y - position.Y) > HandleSize)
                    continue;
                BeginDrag(_selected, c);
                return;
            }
        }
        for (var i = _fields.Count - 1; i >= 0; i--)
        {
            if (!ToScreen(_fields[i]).Contains(position))
                continue;
            SelectionRequested?.Invoke(i);
            BeginDrag(i, -1);
            return;
        }
        if (PictureArea.Contains(position))
        {
            _drawing = true;
            var at = ToShare(position);
            _dragCurrent = new Rect(at, new Size(0, 0));
            InvalidateVisual();
        }
        else
        {
            e.Pointer.Capture(null);
        }
        SelectionRequested?.Invoke(-1);
    }

    private void BeginDrag(int field, int corner)
    {
        _dragField = field;
        _dragCorner = corner;
        _dragStartRect = _fields[field];
        _dragCurrent = _dragStartRect;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_drawing)
        {
            var from = ToShare(_dragStart);
            var to = ToShare(position);
            _dragCurrent = new Rect(from, to).Normalize();
            InvalidateVisual();
            return;
        }
        if (_dragField < 0)
            return;
        var area = PictureArea;
        var dx = (position.X - _dragStart.X) / area.Width;
        var dy = (position.Y - _dragStart.Y) / area.Height;
        var r = _dragStartRect;
        if (_dragCorner < 0)
        {
            _dragCurrent = new Rect(Math.Clamp(r.X + dx, 0, 1 - r.Width), Math.Clamp(r.Y + dy, 0, 1 - r.Height), r.Width, r.Height);
        }
        else
        {
            // The opposite corner stays; the corner being pulled can't pass it
            var (minW, minH) = (MinFieldPixels / area.Width, MinFieldPixels / area.Height);
            double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
            if (_dragCorner is 0 or 3)
                left = Math.Clamp(left + dx, 0, right - minW);
            else
                right = Math.Clamp(right + dx, left + minW, 1);
            if (_dragCorner is 0 or 1)
                top = Math.Clamp(top + dy, 0, bottom - minH);
            else
                bottom = Math.Clamp(bottom + dy, top + minH, 1);
            _dragCurrent = new Rect(left, top, right - left, bottom - top);
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var (drawing, field, result) = (_drawing, _dragField, _dragCurrent);
        _drawing = false;
        _dragField = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
        var area = PictureArea;
        if (drawing && result.Width * area.Width >= MinFieldPixels && result.Height * area.Height >= MinFieldPixels)
            FieldDrawn?.Invoke(result);
        else if (field >= 0 && result != _dragStartRect)
            FieldChanged?.Invoke(field, result);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drawing = false;
        _dragField = -1;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back && _selected >= 0)
        {
            DeleteRequested?.Invoke();
            e.Handled = true;
        }
    }
}
