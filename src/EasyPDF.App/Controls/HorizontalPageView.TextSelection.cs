using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;

namespace EasyPDF.App.Controls;

/// <summary>
/// Selecting text with the select-text tool: dragging selects words in reading order, from the word nearest to the
/// press to the word nearest to the pointer, also across pages (the pages in between completely). Near the edge of
/// the view the content scrolls on by itself, so pages outside the view can be reached. The words are the recognized
/// ones (not saved yet) or the text layer of the file, so the selection shows which text a page has. Double click
/// selects one word.
/// </summary>
public sealed partial class HorizontalPageView
{
    private const double AutoScrollZone = 40;  // px at the edge of the view where a text drag scrolls on
    private const double AutoScrollStep = 30;  // px per tick at the very edge (and beyond)

    /// <summary>True while the select-text tool is on: a drag selects text instead of moving the content.</summary>
    public static readonly StyledProperty<bool> IsSelectTextToolActiveProperty =
        AvaloniaProperty.Register<HorizontalPageView, bool>(nameof(IsSelectTextToolActive));

    public bool IsSelectTextToolActive
    {
        get => GetValue(IsSelectTextToolActiveProperty);
        set => SetValue(IsSelectTextToolActiveProperty, value);
    }

    /// <summary>The selected text changed: the text with line breaks as on the pages (null = nothing selected).</summary>
    public event Action<string?>? TextSelectionChanged;

    /// <summary>The select-text tool was pressed on a page without any text (page index).</summary>
    public event Action<int>? PageWithoutTextClicked;

    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x1E, 0x90, 0xFF));

    /// <summary>The words of a page as loaded for <paramref name="Entry"/> (a changed entry loads them again).</summary>
    private sealed record PageWords(PageEntry Entry, IReadOnlyList<OcrWord> Words);

    /// <summary>A place in the text: page index and word index (0 on a page without words).</summary>
    private readonly record struct TextPos(int Page, int Word) : IComparable<TextPos>
    {
        public int CompareTo(TextPos other) => Page != other.Page ? Page.CompareTo(other.Page) : Word.CompareTo(other.Word);
    }

    /// <summary>
    /// Selected words from <c>First</c> to <c>Last</c> (both included, in reading order); <c>Entries</c> are the pages
    /// <c>First.Page..Last.Page</c> as they were when selected, so a changed document drops the selection.
    /// </summary>
    private sealed record TextRange(TextPos First, TextPos Last, PageEntry[] Entries);

    private readonly Dictionary<int, PageWords> _words = []; // by page id
    private readonly Dictionary<PageEntry, Task> _wordsLoading = [];
    private TextRange? _textRange;
    private bool _selectingText;
    private Point _selectStart;   // in content coordinates, so it stays put while the content scrolls
    private Point _selectPointer; // last pointer position in the view
    private DispatcherTimer? _autoScroll;
    private int _pressedPageId = -1; // page pressed on before its words were loaded

    private int IndexOfPage(int pageId)
    {
        var pages = _document?.Pages;
        for (var i = 0; pages != null && i < pages.Count; i++)
            if (pages[i].Id == pageId)
                return i;
        return -1;
    }

    /// <summary>The selected text (null = none); for tests and the window.</summary>
    public string? SelectedText { get; private set; }

    private void InitTextSelection()
    {
        IsSelectTextToolActiveProperty.Changed.AddClassHandler<HorizontalPageView>((v, _) =>
        {
            v._surface.Cursor = null;
            if (v.IsSelectTextToolActive)
                v.LoadVisibleWords();
            else
                v.ClearTextSelection();
        });
    }

    /// <summary>The words of a page if loaded for its current entry; otherwise starts loading them and returns null.</summary>
    private PageWords? WordsOf(int index)
    {
        var pages = _document?.Pages;
        if (pages == null || index < 0 || index >= pages.Count)
            return null;
        var entry = pages[index];
        if (_words.TryGetValue(entry.Id, out var known) && ReferenceEquals(known.Entry, entry))
            return known;
        if (!_wordsLoading.ContainsKey(entry))
        {
            // Recognized words are there at once (the task completes right away), a text layer comes from the worker
            var loading = LoadWordsAsync(entry, _generation);
            if (!loading.IsCompleted)
                _wordsLoading[entry] = loading;
            else if (_words.TryGetValue(entry.Id, out var loaded) && ReferenceEquals(loaded.Entry, entry))
                return loaded;
        }
        return null;
    }

    private async Task LoadWordsAsync(PageEntry entry, int generation)
    {
        IReadOnlyList<OcrWord> words;
        try
        {
            words = entry.OcrWords.Count > 0 ? entry.OcrWords : await entry.Source.ReadWordsAsync(entry.SourceIndex);
            words = Redaction.VisibleWords(words, entry.RemovalAreas); // blacked-out and pasted-over words are gone once saved
        }
        catch (Exception)
        {
            words = []; // closed or broken page: no text
        }
        _wordsLoading.Remove(entry);
        if (generation != _generation)
            return;
        _words[entry.Id] = new PageWords(entry, words);
        if (_pressedPageId == entry.Id)
        {
            _pressedPageId = -1;
            if (words.Count == 0 && IndexOfPage(entry.Id) is >= 0 and var pressed)
                PageWithoutTextClicked?.Invoke(pressed);
        }
        // A drag waiting for these words goes on; a selection over this page gets its text now
        if (_selectingText)
            UpdateTextSelection(_selectPointer, force: true);
        else if (_textRange is { } range)
            SetTextRange(range, force: true);
        _surface.InvalidateVisual();
    }

    /// <summary>Loads the words of the visible pages ahead, so the cursor shows where text is.</summary>
    private void LoadVisibleWords()
    {
        if (!IsSelectTextToolActive && ActiveRedaction == null || _document == null || _layout.Count != _document.PageCount)
            return;
        var (first, last) = _layout.VisibleRange(_scrollX, Viewport.Width);
        for (var i = first; i <= last; i++)
            WordsOf(i);
    }

    private void ForgetWords()
    {
        _words.Clear();
        _wordsLoading.Clear();
        ClearTextSelection();
    }

    /// <summary>After a document change: a selection over pages that changed, moved or are gone is dropped.</summary>
    private void ValidateTextSelection()
    {
        if (_textRange is not { } range)
            return;
        var pages = _document?.Pages;
        var unchanged = pages != null && range.Last.Page < pages.Count;
        for (var i = 0; unchanged && i < range.Entries.Length; i++)
            unchanged = ReferenceEquals(pages![range.First.Page + i], range.Entries[i]);
        if (!unchanged)
            ClearTextSelection();
    }

    /// <summary>A word's box in the page as shown (points).</summary>
    private static RectPt ShownBox(PageEntry entry, OcrWord word) => PageGeometry.Rotate(word.Box, entry.BaseSize, entry.ExtraRotation);

    /// <summary>A content position in the page as shown (points).</summary>
    private Point ToPagePoint(int index, Point content)
    {
        var b = _layout.PageBounds[index];
        var pxPerPt = b.Width / _document!.Pages[index].DisplaySize.Width;
        return new Point((content.X - b.X) / pxPerPt, (content.Y - b.Y) / pxPerPt);
    }

    private Point ToContent(Point viewport) => new(_scrollX + viewport.X, _scrollY + viewport.Y);

    /// <summary>
    /// The word under <paramref name="pt"/> (page points); with <paramref name="nearest"/> the closest word if none is hit
    /// (a word on the same line counts as closer than one above or below). -1 = none.
    /// </summary>
    private static int WordAt(PageWords words, Point pt, bool nearest)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < words.Words.Count; i++)
        {
            var r = ShownBox(words.Entry, words.Words[i]);
            var dx = pt.X < r.X ? r.X - pt.X : pt.X > r.Right ? pt.X - r.Right : 0;
            var dy = pt.Y < r.Y ? r.Y - pt.Y : pt.Y > r.Bottom ? pt.Y - r.Bottom : 0;
            if (dx == 0 && dy == 0)
                return i;
            var distance = dx + dy * 4;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return nearest ? best : -1;
    }

    /// <summary>
    /// The place in the text nearest to a content position: on the page there (between two pages and beside them the
    /// page to the left, before the first page the first one); null while that page's words are still loading.
    /// </summary>
    private TextPos? TextPosAt(Point content)
    {
        if (_document == null || _layout.Count == 0)
            return null;
        var index = Math.Clamp(_layout.FindPageAt(content.X), 0, _document.PageCount - 1);
        if (WordsOf(index) is not { } words)
            return null;
        return new TextPos(index, words.Words.Count == 0 ? 0 : WordAt(words, ToPagePoint(index, content), nearest: true));
    }

    /// <summary>Left press with the select-text tool.</summary>
    private void BeginTextSelection(Point position, int clickCount)
    {
        var index = PageIndexAt(position);
        ClearTextSelection();
        if (index < 0)
            return;
        MarkedPageId = _document!.Pages[index].Id;
        var words = WordsOf(index);
        _pressedPageId = words == null ? MarkedPageId : -1; // still loading: told when the words arrive
        if (words is { Words.Count: 0 })
            PageWithoutTextClicked?.Invoke(index); // a drag may still go on to pages with text
        else if (clickCount == 2 && words != null && WordAt(words, ToPagePoint(index, ToContent(position)), nearest: false) is >= 0 and var word)
        {
            SetTextRange(new TextRange(new TextPos(index, word), new TextPos(index, word), [words.Entry]));
            return;
        }
        _selectingText = true;
        _selectStart = ToContent(position);
        _selectPointer = position;
    }

    /// <summary>The pointer moved while selecting (or the content scrolled under it): selects up to the pointer.</summary>
    private void UpdateTextSelection(Point position, bool force = false)
    {
        _selectPointer = position;
        var content = ToContent(position);
        var delta = content - _selectStart;
        if (!force && _textRange == null && Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) < PanStartDistance)
            return;
        UpdateAutoScroll(position);
        if (TextPosAt(_selectStart) is not { } anchor || TextPosAt(content) is not { } focus)
            return; // words still loading: their arrival calls this again
        var (first, last) = anchor.CompareTo(focus) <= 0 ? (anchor, focus) : (focus, anchor);
        var entries = new PageEntry[last.Page - first.Page + 1];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = _document!.Pages[first.Page + i];
        SetTextRange(new TextRange(first, last, entries));
    }

    private void EndTextSelection()
    {
        _selectingText = false;
        StopAutoScroll();
    }

    /// <summary>Scrolls on while the pointer is near (or beyond) an edge of the view during a text drag.</summary>
    private void UpdateAutoScroll(Point position)
    {
        if (AutoScrollDelta(position) == default)
        {
            StopAutoScroll();
            return;
        }
        if (_autoScroll != null)
            return;
        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _autoScroll.Tick += (_, _) =>
        {
            var d = AutoScrollDelta(_selectPointer);
            if (!_selectingText || d == default)
            {
                StopAutoScroll();
                return;
            }
            SetScroll(_scrollX + d.X, _scrollY + d.Y);
            UpdateTextSelection(_selectPointer, force: true);
        };
        _autoScroll.Start();
    }

    private Vector AutoScrollDelta(Point p)
    {
        static double Axis(double pos, double size)
        {
            if (pos < AutoScrollZone)
                return -AutoScrollStep * Math.Min(1, (AutoScrollZone - pos) / AutoScrollZone);
            if (pos > size - AutoScrollZone)
                return AutoScrollStep * Math.Min(1, (pos - (size - AutoScrollZone)) / AutoScrollZone);
            return 0;
        }
        return new Vector(Axis(p.X, Viewport.Width), Axis(p.Y, Viewport.Height));
    }

    private void StopAutoScroll()
    {
        _autoScroll?.Stop();
        _autoScroll = null;
    }

    /// <summary>Selects the whole text of the marked page (or of the current one, if no page is marked).</summary>
    public async Task SelectAllTextAsync()
    {
        var index = HighlightedPage >= 0 ? HighlightedPage : CurrentPage;
        if (_document == null || index < 0 || index >= _document.PageCount)
            return;
        var entry = _document.Pages[index];
        var words = WordsOf(index);
        if (words == null && _wordsLoading.TryGetValue(entry, out var loading))
        {
            await loading;
            words = _document != null && index < _document.PageCount && ReferenceEquals(_document.Pages[index], entry) ? WordsOf(index) : null;
        }
        if (words == null || words.Words.Count == 0)
        {
            ClearTextSelection();
            PageWithoutTextClicked?.Invoke(index);
            return;
        }
        SetTextRange(new TextRange(new TextPos(index, 0), new TextPos(index, words.Words.Count - 1), [entry]));
    }

    /// <summary>The selected words of a page (indices from..to), or null if none of them is selected.</summary>
    private (int From, int To)? SelectedWordsOn(int index, PageWords words)
    {
        if (_textRange is not { } range || index < range.First.Page || index > range.Last.Page || words.Words.Count == 0)
            return null;
        var from = index == range.First.Page ? range.First.Word : 0;
        var to = index == range.Last.Page ? range.Last.Word : words.Words.Count - 1;
        return from <= to ? (from, Math.Min(to, words.Words.Count - 1)) : null;
    }

    private void SetTextRange(TextRange? range, bool force = false)
    {
        if (!force && range == _textRange)
            return;
        _textRange = range;
        // The text page by page, pages separated by an empty line (as when copying whole pages)
        var sb = new StringBuilder();
        for (var index = range?.First.Page ?? 0; range != null && index <= range.Last.Page; index++)
        {
            if (!_words.TryGetValue(range.Entries[index - range.First.Page].Id, out var words) || SelectedWordsOn(index, words) is not var (from, to))
                continue; // a page in between that is still loading adds its text when it arrives
            var selected = new List<OcrWord>();
            for (var i = from; i <= to; i++)
                selected.Add(words.Words[i]);
            var text = PageText.FromWords(selected, words.Entry.BaseSize, words.Entry.ExtraRotation);
            if (text.Length == 0)
                continue;
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(text);
        }
        var changed = (sb.Length == 0 ? null : sb.ToString()) is var newText && newText != SelectedText;
        SelectedText = newText;
        _surface.InvalidateVisual();
        if (changed)
            TextSelectionChanged?.Invoke(SelectedText);
    }

    /// <summary>Removes the text selection (Esc, handled by the window's shortcut).</summary>
    public void ClearTextSelection()
    {
        EndTextSelection();
        if (_textRange != null || SelectedText != null)
            SetTextRange(null);
    }

    /// <summary>I-beam over text, the normal arrow elsewhere: moving the mouse shows which parts of a page have text.</summary>
    private void UpdateTextCursor(Point position)
    {
        var index = PageIndexAt(position);
        var overText = index >= 0 && WordsOf(index) is { } words && WordAt(words, ToPagePoint(index, ToContent(position)), nearest: false) >= 0;
        _surface.Cursor = overText ? new Cursor(StandardCursorType.Ibeam) : null;
    }

    private void DrawTextSelection(DrawingContext context, int index, PageEntry page, Rect target)
    {
        if (_textRange == null || !_words.TryGetValue(page.Id, out var words) || !ReferenceEquals(words.Entry, page)
            || SelectedWordsOn(index, words) is not var (from, to))
            return;
        var pxPerPt = target.Width / page.DisplaySize.Width;
        Rect ToScreen(RectPt r) => new(target.X + r.X * pxPerPt, target.Y + r.Y * pxPerPt, r.Width * pxPerPt, r.Height * pxPerPt);
        RectPt? previous = null;
        using (context.PushClip(target))
            for (var i = from; i <= to; i++)
            {
                var box = ShownBox(page, words.Words[i]);
                // The gap to the previous word on the same line is filled too, so a line reads as one band
                if (previous is { } p && box.X > p.Right)
                {
                    var top = Math.Max(p.Y, box.Y);
                    var bottom = Math.Min(p.Bottom, box.Bottom);
                    if (bottom - top >= Math.Min(p.Height, box.Height) * 0.5)
                        context.FillRectangle(SelectionBrush, ToScreen(new RectPt(p.Right, top, box.X - p.Right, bottom - top)));
                }
                context.FillRectangle(SelectionBrush, ToScreen(box));
                previous = box;
            }
    }
}
