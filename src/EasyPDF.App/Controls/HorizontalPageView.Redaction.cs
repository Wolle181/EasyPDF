using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.Controls;

/// <summary>
/// Black out and white out: with the tool on, a drag that starts on a word selects text (as the select-text tool does)
/// and covers the selected words line by line when released; a drag that starts elsewhere draws a box around an area.
/// The boxes are drawn opaque, as they will look in the saved file; white ones get a thin outline on screen only.
/// Without a tool, a box can be selected, moved and resized like a stamp (freely, not keeping its aspect ratio).
/// </summary>
public sealed partial class HorizontalPageView
{
    private const double MinRedactionPt = 2;

    /// <summary>The black-out or white-out tool that is on (null = none).</summary>
    public static readonly StyledProperty<RedactionFill?> ActiveRedactionProperty =
        AvaloniaProperty.Register<HorizontalPageView, RedactionFill?>(nameof(ActiveRedaction));

    public RedactionFill? ActiveRedaction
    {
        get => GetValue(ActiveRedactionProperty);
        set => SetValue(ActiveRedactionProperty, value);
    }

    /// <summary>Asks to cover areas: page index and rect in the page as shown (points, upright in the view), and the colour.</summary>
    public event Action<IReadOnlyList<(int PageIndex, RectPt ViewRect)>, RedactionFill>? RedactionPlaceRequested;

    /// <summary>A box was moved or resized: its id and the new rectangle in page space.</summary>
    public event Action<int, RectPt>? RedactionChangeRequested;

    /// <summary>An area being drawn with the tool: page index, where the drag started and the rect so far (page as shown, points).</summary>
    private sealed record AreaDrag(int PageIndex, Point Start, RectPt Current);

    private AreaDrag? _areaDrag;
    private bool _redactingText; // the running text selection belongs to the tool and is covered when released

    private static readonly IBrush CoverOutline = new SolidColorBrush(Color.FromArgb(0x90, 0x70, 0x78, 0x88));
    private static readonly IBrush DraftFill = new SolidColorBrush(Color.FromArgb(0x50, 0x1E, 0x90, 0xFF));

    private static IBrush FillOf(RedactionFill fill) => fill == RedactionFill.White ? Brushes.White : Brushes.Black;

    /// <summary>Left press with a black-out or white-out tool: on a word selects text, elsewhere starts a box.</summary>
    private void BeginRedaction(Point position, int clickCount)
    {
        var index = PageIndexAt(position);
        if (index < 0 || _document == null)
            return;
        MarkedPageId = _document.Pages[index].Id;
        if (WordsOf(index) is { } words && WordAt(words, ToPagePoint(index, ToContent(position)), nearest: false) >= 0)
        {
            _redactingText = true;
            BeginTextSelection(position, clickCount);
            if (_textRange != null && !_selectingText)
                CoverSelectedText(); // double click: that word
            return;
        }
        var start = ToPagePoint(index, ToContent(position));
        _areaDrag = new AreaDrag(index, start, new RectPt(start.X, start.Y, 0, 0));
    }

    private void UpdateAreaDrag(Point position)
    {
        if (_areaDrag is not { } drag || _document == null)
            return;
        var size = _document.Pages[drag.PageIndex].DisplaySize;
        var p = ToPagePoint(drag.PageIndex, ToContent(position));
        double x = Math.Clamp(p.X, 0, size.Width), y = Math.Clamp(p.Y, 0, size.Height);
        _areaDrag = drag with
        {
            Current = new RectPt(Math.Min(x, drag.Start.X), Math.Min(y, drag.Start.Y), Math.Abs(x - drag.Start.X), Math.Abs(y - drag.Start.Y)),
        };
        _surface.InvalidateVisual();
    }

    private void EndAreaDrag(bool commit)
    {
        if (_areaDrag is not { } drag)
            return;
        _areaDrag = null;
        _surface.InvalidateVisual();
        // A plain click draws nothing
        if (!commit || drag.Current.Width < MinRedactionPt || drag.Current.Height < MinRedactionPt)
            return;
        if (ActiveRedaction is { } fill)
            RedactionPlaceRequested?.Invoke([(drag.PageIndex, drag.Current)], fill);
        else if (IsCopyAreaToolActive)
            _ = CopyAreaAsync(drag.PageIndex, drag.Current);
    }

    /// <summary>Ends a text drag of the tool: the selected words are covered.</summary>
    private void EndRedactionText()
    {
        _redactingText = false;
        EndTextSelection();
        CoverSelectedText();
    }

    /// <summary>Black out or white out pressed while text is selected (select-text tool): covers that text.</summary>
    public void RedactSelectedText(RedactionFill fill) => CoverSelectedText(fill);

    private void CoverSelectedText(RedactionFill? fill = null)
    {
        _redactingText = false;
        var bands = SelectedTextBands();
        ClearTextSelection();
        if (bands.Count > 0 && (fill ?? ActiveRedaction) is { } use)
            RedactionPlaceRequested?.Invoke(bands, use);
    }

    /// <summary>
    /// The selected words as one box per line (the gaps between the words included), a little larger than the words so
    /// no edge of a letter shows: page index and rect in the page as shown.
    /// </summary>
    private List<(int PageIndex, RectPt ViewRect)> SelectedTextBands()
    {
        var bands = new List<(int, RectPt)>();
        if (_textRange is not { } range)
            return bands;
        for (var index = range.First.Page; index <= range.Last.Page; index++)
        {
            var entry = range.Entries[index - range.First.Page];
            if (!_words.TryGetValue(entry.Id, out var words) || !ReferenceEquals(words.Entry, entry) || SelectedWordsOn(index, words) is not var (from, to))
                continue;
            var size = entry.DisplaySize;
            RectPt? line = null;
            void Flush()
            {
                if (line is not { } l)
                    return;
                double padX = 1, padY = Math.Max(1, l.Height * 0.15);
                double x0 = Math.Max(0, l.X - padX), y0 = Math.Max(0, l.Y - padY);
                bands.Add((index, new RectPt(x0, y0, Math.Min(size.Width, l.Right + padX) - x0, Math.Min(size.Height, l.Bottom + padY) - y0)));
                line = null;
            }
            for (var i = from; i <= to; i++)
            {
                var box = ShownBox(entry, words.Words[i]);
                if (line is { } l)
                {
                    // Same rule as the selection band on screen: next to the previous word at the same height
                    var overlap = Math.Min(l.Bottom, box.Bottom) - Math.Max(l.Y, box.Y);
                    if (box.X >= l.X && overlap >= Math.Min(l.Height, box.Height) * 0.5)
                    {
                        var x0 = Math.Min(l.X, box.X);
                        var y0 = Math.Min(l.Y, box.Y);
                        line = new RectPt(x0, y0, Math.Max(l.Right, box.Right) - x0, Math.Max(l.Bottom, box.Bottom) - y0);
                        continue;
                    }
                    Flush();
                }
                line = box;
            }
            Flush();
        }
        return bands;
    }

    /// <summary>I-beam over text (a drag covers words), cross elsewhere (a drag draws a box).</summary>
    private void UpdateRedactionCursor(Point position)
    {
        var index = PageIndexAt(position);
        var overText = index >= 0 && WordsOf(index) is { } words && WordAt(words, ToPagePoint(index, ToContent(position)), nearest: false) >= 0;
        _surface.Cursor = new Cursor(overText ? StandardCursorType.Ibeam : StandardCursorType.Cross);
    }

    private void DrawRedactions(DrawingContext context, PageEntry page, Rect target)
    {
        if (page.Redactions.Count == 0)
            return;
        var pxPerPt = target.Width / page.DisplaySize.Width;
        var outline = new Pen(CoverOutline, 1, DashStyle.Dash);
        using (context.PushClip(target))
            foreach (var redaction in page.Redactions)
            {
                var r = _stampDrag is { } drag && drag.Id == redaction.Id ? drag.Current : PageGeometry.Rotate(redaction.Rect, page.BaseSize, page.ExtraRotation);
                var covered = new Rect(target.X + r.X * pxPerPt, target.Y + r.Y * pxPerPt, r.Width * pxPerPt, r.Height * pxPerPt);
                context.FillRectangle(FillOf(redaction.Fill), covered);
                if (_capturing)
                    continue; // copied: as it will look in the file
                if (redaction.Fill == RedactionFill.White)
                    context.DrawRectangle(outline, covered.Deflate(0.5));
                if (redaction.Id == SelectedStampId)
                    DrawSelection(context, covered);
            }
    }

    private void DrawAreaDraft(DrawingContext context)
    {
        if (_areaDrag is not { } drag || _document == null || drag.PageIndex >= _layout.Count)
            return;
        var screen = StampScreenRect(drag.PageIndex, drag.Current);
        context.FillRectangle(DraftFill, screen);
        context.DrawRectangle(new Pen(Brushes.DodgerBlue, 1, DashStyle.Dash), screen);
    }
}
