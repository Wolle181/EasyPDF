using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Core.Documents;

/// <summary>One page of the edited document: a page of a source file plus an additional rotation.</summary>
public sealed record PageEntry(int Id, IPdfSource Source, int SourceIndex, int ExtraRotation)
{
    /// <summary>Size on screen: the source page size turned by <see cref="ExtraRotation"/>.</summary>
    public PageSizePt DisplaySize => BaseSize.Rotated(ExtraRotation);

    /// <summary>The source page size (with its own /Rotate) before the extra rotation: the space the stamps live in.</summary>
    public PageSizePt BaseSize => Source.GetPageSizeOrEstimate(SourceIndex);

    /// <summary>Pictures placed on this page that are not saved yet.</summary>
    public IReadOnlyList<StampOverlay> Stamps { get; init; } = [];

    /// <summary>Texts placed on this page that are not saved yet.</summary>
    public IReadOnlyList<TextOverlay> Texts { get; init; } = [];

    /// <summary>Recognized words (OCR) that are written as an invisible text layer when saving; in page space like the overlays.</summary>
    public IReadOnlyList<OcrWord> OcrWords { get; init; } = [];

    /// <summary>Areas to black out or cover in white; what lies under them is removed when saving.</summary>
    public IReadOnlyList<RedactionOverlay> Redactions { get; init; } = [];

    /// <summary>
    /// Everything under which the content is removed when saving: the redactions, and the pasted areas as white boxes
    /// (<see cref="StampOverlay.CoversContent"/>; their own picture is put on top afterwards).
    /// </summary>
    public IReadOnlyList<RedactionOverlay> RemovalAreas => Stamps.Any(s => s.CoversContent)
        ? [.. Redactions, .. Stamps.Where(s => s.CoversContent).Select(s => new RedactionOverlay(s.Id, s.Rect, RedactionFill.White))]
        : Redactions;
}

public enum DocumentChangeKind
{
    /// <summary>Pages were moved/rotated/deleted/inserted or undo/redo was applied.</summary>
    Edited,

    /// <summary>The whole content was replaced (after saving the file is reopened).</summary>
    Reloaded,
}

/// <summary>
/// Non-destructive page list over one or more PDF sources. Every edit is one undoable step;
/// the files on disk only change in <see cref="SaveAsync"/>.
/// Each state is an immutable snapshot, so undo/redo just swaps snapshots.
/// </summary>
public sealed class EditableDocument : IAsyncDisposable
{
    private static int _nextId;

    private readonly Stack<PageEntry[]> _undo = new();
    private readonly Stack<PageEntry[]> _redo = new();
    private List<IPdfSource> _sources;
    private PageEntry[] _pages;
    private PageEntry[]? _savedState; // null = never saved

    private EditableDocument(List<IPdfSource> sources, PageEntry[] pages, IPdfSource baseSource, bool hasFile)
    {
        _sources = sources;
        _pages = pages;
        BaseSource = baseSource;
        HasFile = hasFile;
        _savedState = hasFile ? pages : null;
    }

    /// <summary>Opens a document showing all pages of <paramref name="source"/>; takes ownership of the source.</summary>
    public static EditableDocument FromSource(IPdfSource source) =>
        new([source], AllPages(source), source, hasFile: true);

    /// <summary>
    /// Builds a new unsaved document from the current pages of several documents (in the given order).
    /// Takes ownership of all their sources – the parts must not be used or disposed afterwards.
    /// The first part's file is the base for saving (its bookmarks are kept).
    /// </summary>
    public static EditableDocument Combine(IReadOnlyList<EditableDocument> parts)
    {
        if (parts.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(parts));
        var sources = parts.SelectMany(p => p._sources).Distinct().ToList();
        var pages = parts.SelectMany(p => p._pages).ToArray();
        return new EditableDocument(sources, pages, parts[0].BaseSource, hasFile: false);
    }

    /// <summary>
    /// An independent unsaved copy of the current pages (with rotations, stamps, texts and recognized words) on newly opened
    /// sources of the same files, so it stays usable after this document is closed. The copy owns its sources.
    /// </summary>
    public async Task<EditableDocument> CopyAsync(CancellationToken token = default)
    {
        var pages = _pages;
        var copies = new Dictionary<IPdfSource, IPdfSource>();
        try
        {
            foreach (var source in pages.Select(p => p.Source).Prepend(BaseSource).Distinct())
            {
                var copy = await PdfBackend.Current.OpenAsync(source.Path, token);
                // Blank-page files: whichever document is closed last removes the file (the other still holds it open)
                copy.DeleteFileOnDispose = source.DeleteFileOnDispose;
                copies[source] = copy;
            }
        }
        catch
        {
            foreach (var copy in copies.Values)
                await copy.DisposeAsync();
            throw;
        }
        PageEntry[] copiedPages = [.. pages.Select(p => p with { Id = Interlocked.Increment(ref _nextId), Source = copies[p.Source] })];
        return new EditableDocument([.. copies.Values], copiedPages, copies[BaseSource], hasFile: false);
    }

    private static PageEntry[] AllPages(IPdfSource source) =>
        [.. Enumerable.Range(0, source.PageCount).Select(i => NewEntry(source, i))];

    private static PageEntry NewEntry(IPdfSource source, int index) =>
        new(Interlocked.Increment(ref _nextId), source, index, 0);

    public IReadOnlyList<PageEntry> Pages => _pages;

    public int PageCount => _pages.Length;

    public IReadOnlyList<IPdfSource> Sources => _sources;

    /// <summary>The file whose structure (bookmarks, metadata) is kept when saving.</summary>
    public IPdfSource BaseSource { get; private set; }

    /// <summary>False for documents that were never saved (e.g. concatenated ones).</summary>
    public bool HasFile { get; private set; }

    public string? FilePath => HasFile ? BaseSource.Path : null;

    /// <summary>True while any page carries a placed stamp.</summary>
    public bool HasStamps => _pages.Any(p => p.Stamps.Count > 0);

    /// <summary>True while any page has an area under which content is removed when saving (redactions, pasted areas).</summary>
    public bool RemovesContent => _pages.Any(p => p.RemovalAreas.Count > 0);

    /// <summary>Number of pages with areas to black out or cover that are not saved yet.</summary>
    public int RedactedPageCount => _pages.Count(p => p.Redactions.Count > 0);

    /// <summary>Number of pages with recognized words that are not saved yet.</summary>
    public int OcrPageCount => _pages.Count(p => p.OcrWords.Count > 0);

    public bool IsDirty => !ReferenceEquals(_pages, _savedState);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public event Action<DocumentChangeKind>? Changed;

    #region Editing

    private void Apply(PageEntry[] newPages)
    {
        _undo.Push(_pages);
        _redo.Clear();
        _pages = newPages;
        Changed?.Invoke(DocumentChangeKind.Edited);
    }

    private int[] Normalize(IEnumerable<int> indices)
    {
        var list = indices.Distinct().OrderBy(i => i).ToArray();
        if (list.Length > 0 && (list[0] < 0 || list[^1] >= _pages.Length))
            throw new ArgumentOutOfRangeException(nameof(indices));
        return list;
    }

    /// <summary>
    /// Moves the pages to before position <paramref name="insertIndex"/> (0..PageCount, in terms of the list before the move).
    /// The moved pages keep their relative order. Returns the new index of the first moved page.
    /// </summary>
    public int Move(IEnumerable<int> indices, int insertIndex)
    {
        var selected = Normalize(indices);
        if (selected.Length == 0)
            return -1;
        if (insertIndex < 0 || insertIndex > _pages.Length)
            throw new ArgumentOutOfRangeException(nameof(insertIndex));

        var target = insertIndex - selected.Count(i => i < insertIndex);
        var set = new HashSet<int>(selected);
        var rest = _pages.Where((_, i) => !set.Contains(i)).ToList();
        rest.InsertRange(target, selected.Select(i => _pages[i]));
        var result = rest.ToArray();
        if (result.SequenceEqual(_pages))
            return target;
        Apply(result);
        return target;
    }

    /// <summary>Rotates the pages by 90, -90 or 180 degrees (any multiple of 90 works) as one undo step.</summary>
    public void Rotate(IEnumerable<int> indices, int degrees)
    {
        if (degrees % 90 != 0)
            throw new ArgumentException("Rotation must be a multiple of 90 degrees.", nameof(degrees));
        var selected = Normalize(indices);
        if (selected.Length == 0 || ((degrees % 360) + 360) % 360 == 0)
            return;

        var result = (PageEntry[])_pages.Clone();
        foreach (var i in selected)
            result[i] = result[i] with { ExtraRotation = (((result[i].ExtraRotation + degrees) % 360) + 360) % 360 };
        Apply(result);
    }

    private static int _nextStampId;

    /// <summary>
    /// Places a picture on a page as one undo step. <paramref name="rect"/> is in page space (see <see cref="StampOverlay"/>).
    /// With <paramref name="coversContent"/> it is a pasted area (<see cref="StampOverlay.CoversContent"/>). Returns the stamp.
    /// </summary>
    public StampOverlay AddStamp(int pageIndex, byte[] image, int pixelWidth, int pixelHeight, RectPt rect, int rotation = 0, byte[]? bgra = null,
        bool coversContent = false, StampTemplate? template = null)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Length)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var stamp = new StampOverlay(Interlocked.Increment(ref _nextStampId), image, pixelWidth, pixelHeight, rect, ((rotation % 360) + 360) % 360, bgra,
            coversContent, template);
        var result = (PageEntry[])_pages.Clone();
        result[pageIndex] = result[pageIndex] with { Stamps = [.. result[pageIndex].Stamps, stamp] };
        Apply(result);
        return stamp;
    }

    /// <summary>Places a text on a page as one undo step. <paramref name="rect"/> is in page space (see <see cref="TextOverlay"/>).</summary>
    public TextOverlay AddText(int pageIndex, string text, string font, double fontSize, string color, RectPt rect, int rotation = 0,
        bool bold = false, bool italic = false)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Length)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var overlay = new TextOverlay(Interlocked.Increment(ref _nextStampId), text, font, fontSize, color, rect, ((rotation % 360) + 360) % 360,
            bold, italic);
        var result = (PageEntry[])_pages.Clone();
        result[pageIndex] = result[pageIndex] with { Texts = [.. result[pageIndex].Texts, overlay] };
        Apply(result);
        return overlay;
    }

    /// <summary>Changes the content and area of a placed text (one undo step). Returns false if the text is not in the document.</summary>
    public bool UpdateText(int textId, string text, RectPt rect)
    {
        var index = Array.FindIndex(_pages, p => p.Texts.Any(t => t.Id == textId));
        if (index < 0)
            return false;
        var result = (PageEntry[])_pages.Clone();
        result[index] = result[index] with { Texts = [.. result[index].Texts.Select(t => t.Id == textId ? t with { Text = text, Rect = rect } : t)] };
        Apply(result);
        return true;
    }

    /// <summary>Moves/scales a placed text (one undo step): new area and font size, the content stays. Returns false if the text is not in the document.</summary>
    public bool UpdateText(int textId, RectPt rect, double fontSize)
    {
        var index = Array.FindIndex(_pages, p => p.Texts.Any(t => t.Id == textId));
        if (index < 0)
            return false;
        var result = (PageEntry[])_pages.Clone();
        result[index] = result[index] with { Texts = [.. result[index].Texts.Select(t => t.Id == textId ? t with { Rect = rect, FontSize = fontSize } : t)] };
        Apply(result);
        return true;
    }

    /// <summary>Removes a placed text (one undo step). Returns false if the text is not in the document.</summary>
    public bool RemoveText(int textId)
    {
        var index = Array.FindIndex(_pages, p => p.Texts.Any(t => t.Id == textId));
        if (index < 0)
            return false;
        var result = (PageEntry[])_pages.Clone();
        result[index] = result[index] with { Texts = [.. result[index].Texts.Where(t => t.Id != textId)] };
        Apply(result);
        return true;
    }

    /// <summary>Moves/resizes a placed stamp (one undo step). Returns false if the stamp is not in the document.</summary>
    public bool UpdateStamp(int stampId, RectPt rect)
    {
        var index = Array.FindIndex(_pages, p => p.Stamps.Any(s => s.Id == stampId));
        if (index < 0)
            return false;
        var result = (PageEntry[])_pages.Clone();
        result[index] = result[index] with { Stamps = [.. result[index].Stamps.Select(s => s.Id == stampId ? s with { Rect = rect } : s)] };
        Apply(result);
        return true;
    }

    /// <summary>
    /// Gives a placed stamp a new picture (one undo step), position and size stay. It gets a new id, because the id stands
    /// for one picture: what shows the stamp keeps its bitmap by id, also across undo and redo. Returns the changed stamp,
    /// null if it is not in the document.
    /// </summary>
    public StampOverlay? ReplaceStampPicture(int stampId, byte[] image, int pixelWidth, int pixelHeight, byte[] bgra, StampTemplate? template)
    {
        var index = Array.FindIndex(_pages, p => p.Stamps.Any(s => s.Id == stampId));
        if (index < 0)
            return null;
        var changed = _pages[index].Stamps.First(s => s.Id == stampId) with
        {
            Id = Interlocked.Increment(ref _nextStampId),
            Image = image,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            Bgra = bgra,
            Template = template,
        };
        var result = (PageEntry[])_pages.Clone();
        result[index] = result[index] with { Stamps = [.. result[index].Stamps.Select(s => s.Id == stampId ? changed : s)] };
        Apply(result);
        return changed;
    }

    /// <summary>Removes a placed stamp (one undo step). Returns false if the stamp is not in the document.</summary>
    public bool RemoveStamp(int stampId)
    {
        var index = Array.FindIndex(_pages, p => p.Stamps.Any(s => s.Id == stampId));
        if (index < 0)
            return false;
        var result = (PageEntry[])_pages.Clone();
        result[index] = result[index] with { Stamps = [.. result[index].Stamps.Where(s => s.Id != stampId)] };
        Apply(result);
        return true;
    }

    /// <summary>
    /// Blacks out or covers areas as one undo step (also several on different pages, e.g. a text selection over a page
    /// break). The rects are in page space (see <see cref="RedactionOverlay"/>). Returns the new redactions.
    /// </summary>
    public IReadOnlyList<RedactionOverlay> AddRedactions(IReadOnlyList<(int PageIndex, RectPt Rect)> areas, RedactionFill fill)
    {
        if (areas.Any(a => a.PageIndex < 0 || a.PageIndex >= _pages.Length))
            throw new ArgumentOutOfRangeException(nameof(areas));
        var added = new List<RedactionOverlay>();
        var result = (PageEntry[])_pages.Clone();
        foreach (var (pageIndex, rect) in areas)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;
            var redaction = new RedactionOverlay(Interlocked.Increment(ref _nextStampId), rect, fill);
            result[pageIndex] = result[pageIndex] with { Redactions = [.. result[pageIndex].Redactions, redaction] };
            added.Add(redaction);
        }
        if (added.Count > 0)
            Apply(result);
        return added;
    }

    /// <summary>Moves/resizes a redaction (one undo step). Returns false if it is not in the document.</summary>
    public bool UpdateRedaction(int redactionId, RectPt rect)
    {
        var index = Array.FindIndex(_pages, p => p.Redactions.Any(r => r.Id == redactionId));
        if (index < 0 || rect.Width <= 0 || rect.Height <= 0)
            return false;
        var result = (PageEntry[])_pages.Clone();
        result[index] = result[index] with { Redactions = [.. result[index].Redactions.Select(r => r.Id == redactionId ? r with { Rect = rect } : r)] };
        Apply(result);
        return true;
    }

    /// <summary>Removes a redaction that is not saved yet (one undo step). Returns false if it is not in the document.</summary>
    public bool RemoveRedaction(int redactionId)
    {
        var index = Array.FindIndex(_pages, p => p.Redactions.Any(r => r.Id == redactionId));
        if (index < 0)
            return false;
        var result = (PageEntry[])_pages.Clone();
        result[index] = result[index] with { Redactions = [.. result[index].Redactions.Where(r => r.Id != redactionId)] };
        Apply(result);
        return true;
    }

    /// <summary>
    /// Attaches recognized words to pages, keyed by <see cref="PageEntry.Id"/> (OCR runs in the background, the pages may
    /// have moved meanwhile), as one undo step. Earlier OCR words of those pages are replaced; pages no longer in the
    /// document are skipped. Returns the number of pages changed (0 = nothing changed, no undo step).
    /// </summary>
    public int SetOcrWords(IReadOnlyDictionary<int, IReadOnlyList<OcrWord>> wordsByPageId)
    {
        var result = (PageEntry[])_pages.Clone();
        var changed = 0;
        for (var i = 0; i < result.Length; i++)
        {
            if (!wordsByPageId.TryGetValue(result[i].Id, out var words) || (words.Count == 0 && result[i].OcrWords.Count == 0))
                continue;
            result[i] = result[i] with { OcrWords = [.. words] };
            changed++;
        }
        if (changed > 0)
            Apply(result);
        return changed;
    }

    public void Delete(IEnumerable<int> indices)
    {
        var selected = Normalize(indices);
        if (selected.Length == 0)
            return;
        if (selected.Length >= _pages.Length)
            throw new InvalidOperationException("A document needs at least one page.");
        var set = new HashSet<int>(selected);
        Apply([.. _pages.Where((_, i) => !set.Contains(i))]);
    }

    /// <summary>
    /// Inserts all pages of the given sources (in order) before <paramref name="index"/> as one undo step.
    /// Takes ownership of the sources. Returns the index of the first inserted page.
    /// </summary>
    public int InsertFrom(IReadOnlyList<IPdfSource> sources, int index) =>
        Insert(sources, sources.SelectMany(s => AllPages(s)).ToList(), index);

    /// <summary>
    /// Inserts only the given zero-based pages of <paramref name="source"/> (in the given order) before
    /// <paramref name="index"/> as one undo step. Takes ownership of the source. Returns the index of the first inserted page.
    /// </summary>
    public int InsertFrom(IPdfSource source, IReadOnlyList<int> sourcePages, int index)
    {
        if (sourcePages.Any(p => p < 0 || p >= source.PageCount))
            throw new ArgumentOutOfRangeException(nameof(sourcePages));
        return Insert([source], [.. sourcePages.Select(p => NewEntry(source, p))], index);
    }

    /// <summary>
    /// Replaces the <paramref name="count"/> consecutive pages from <paramref name="first"/> on by the given zero-based pages
    /// of <paramref name="replacement"/> (in this order, with their rotations, stamps, texts and recognized words) as one
    /// undo step. Takes ownership of the replacement's sources: it must not be used or disposed afterwards.
    /// Returns the index of the first new page (= <paramref name="first"/>).
    /// </summary>
    public int Replace(int first, int count, EditableDocument replacement, IReadOnlyList<int> replacementPages)
    {
        if (ReferenceEquals(replacement, this))
            throw new ArgumentException("A document cannot replace its own pages.", nameof(replacement));
        if (first < 0 || count < 1 || first + count > _pages.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (replacementPages.Count == 0)
            throw new ArgumentException("At least one page is required.", nameof(replacementPages));
        if (replacementPages.Any(p => p < 0 || p >= replacement.PageCount))
            throw new ArgumentOutOfRangeException(nameof(replacementPages));
        // New ids: a page taken twice must not share its id (the selection works by id)
        var inserted = replacementPages.Select(p => replacement._pages[p] with { Id = Interlocked.Increment(ref _nextId) });
        foreach (var source in replacement._sources.Where(s => !_sources.Contains(s)))
            _sources.Add(source);
        var result = _pages.ToList();
        result.RemoveRange(first, count);
        result.InsertRange(first, inserted);
        Apply([.. result]);
        return first;
    }

    /// <summary>
    /// Inserts one empty page of the given size before <paramref name="index"/> as one undo step (no rotation).
    /// Returns the index of the new page.
    /// </summary>
    // No ConfigureAwait(false) here: the change event at the end must be raised on the caller's (UI) thread
    public async Task<int> InsertBlankAsync(int index, PageSizePt size)
    {
        if (index < 0 || index > _pages.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        var source = await PdfBackend.Current.CreateBlankAsync(size);
        return Insert([source], [NewEntry(source, 0)], index);
    }

    private int Insert(IReadOnlyList<IPdfSource> sources, List<PageEntry> inserted, int index)
    {
        if (index < 0 || index > _pages.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (inserted.Count == 0)
            return index;
        foreach (var s in sources)
            _sources.Add(s);
        var result = _pages.ToList();
        result.InsertRange(index, inserted);
        Apply([.. result]);
        return index;
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;
        _redo.Push(_pages);
        _pages = _undo.Pop();
        Changed?.Invoke(DocumentChangeKind.Edited);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;
        _undo.Push(_pages);
        _pages = _redo.Pop();
        Changed?.Invoke(DocumentChangeKind.Edited);
    }

    #endregion

    #region Saving

    /// <summary>
    /// Writes the document to <paramref name="path"/> (temp file next to it, then replace) and reopens the result:
    /// afterwards the document consists of that single file, the undo history is cleared and <see cref="IsDirty"/> is false.
    /// If saving fails, the document stays as it was. With <paramref name="reduceImages"/> the pictures are scaled down
    /// to make the file smaller; returns how many were.
    /// </summary>
    public async Task<int> SaveAsync(string path, IProgress<double>? progress = null, CancellationToken token = default,
        ImageReduction? reduceImages = null)
    {
        var callerContext = SynchronizationContext.Current; // the continuations below leave the caller's thread
        var (fresh, reduced) = await PdfBackend.Current.SaveAsync(_pages, BaseSource, path, _sources, progress, token, reduceImages)
            .ConfigureAwait(false);
        var old = _sources;
        _sources = [fresh];
        BaseSource = fresh;
        HasFile = true;
        _pages = AllPages(fresh);
        _savedState = _pages;
        _undo.Clear();
        _redo.Clear();
        foreach (var s in old)
            await s.DisposeAsync().ConfigureAwait(false);
        // Listeners (the views) must be told on the caller's (UI) thread, not on the worker thread the awaits above ended on
        if (callerContext != null && callerContext != SynchronizationContext.Current)
            callerContext.Send(_ => Changed?.Invoke(DocumentChangeKind.Reloaded), null);
        else
            Changed?.Invoke(DocumentChangeKind.Reloaded);
        return reduced;
    }

    public static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        // Taken out first: a second call while this one is still closing files finds nothing left to do
        IPdfSource[] sources = [.. _sources];
        _sources.Clear();
        foreach (var s in sources)
            await s.DisposeAsync().ConfigureAwait(false);
    }
}
