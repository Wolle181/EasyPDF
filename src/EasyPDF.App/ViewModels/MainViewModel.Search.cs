using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.App.Localization;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;

namespace EasyPDF.App.ViewModels;

/// <summary>
/// Finding text: Ctrl/Cmd+F opens the search bar above the pages; typing searches the whole document (recognized words
/// or the text layer of the file, as copying text does), all hits are highlighted in the reading view and the first one
/// from the current page on is shown. Enter/F3 goes to the next hit, Shift+Enter/Shift+F3 to the previous one.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The search bar is shown.</summary>
    [ObservableProperty]
    private bool _isSearchOpen;

    /// <summary>The text typed into the search bar.</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>All hits found so far, in document order (a new list on every change, so bindings update).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatusText))]
    private IReadOnlyList<TextSearchHit> _searchHits = [];

    /// <summary>The hit shown last (highlighted more strongly); null = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatusText))]
    private TextSearchHit? _currentSearchHit;

    /// <summary>The search still reads pages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatusText))]
    private bool _isSearching;

    /// <summary>The finished search found no page with any text at all (e.g. only scans).</summary>
    private bool _searchFoundNoText;

    private CancellationTokenSource? _search;

    /// <summary>The words of the pages searched so far; a changed page is a new entry and is read again.</summary>
    private readonly ConditionalWeakTable<PageEntry, IReadOnlyList<OcrWord>> _searchWords = [];

    /// <summary>Asks the window to put the keyboard focus into the search box (and select its text).</summary>
    public event Action? SearchBoxFocusRequested;

    /// <summary>Asks the reading view to bring a hit into view.</summary>
    public event Action<TextSearchHit>? SearchHitShown;

    /// <summary>"3 of 12", "No matches" … next to the search box.</summary>
    public string SearchStatusText
    {
        get
        {
            if (TextSearch.NormalizeQuery(SearchText).Length == 0)
                return "";
            if (SearchHits.Count > 0)
                return CurrentSearchHit is { } hit
                    ? Loc.F("SearchResult", IndexOfHit(hit) + 1, SearchHits.Count)
                    : Loc.F("SearchResultCount", SearchHits.Count);
            if (IsSearching)
                return Loc.T("SearchRunning");
            return Loc.T(_searchFoundNoText ? "SearchNoText" : "SearchNoMatches");
        }
    }

    private int IndexOfHit(TextSearchHit hit)
    {
        for (var i = 0; i < SearchHits.Count; i++)
            if (ReferenceEquals(SearchHits[i], hit))
                return i;
        return -1;
    }

    /// <summary>Ctrl/Cmd+F: opens the search bar (or puts the focus back into it).</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void ShowSearch()
    {
        if (!IsSearchOpen)
        {
            IsSearchOpen = true;
            RestartSearch(navigate: true);
        }
        SearchBoxFocusRequested?.Invoke();
    }

    /// <summary>Esc in the search box or its close button: hides the bar and the highlights (the text is kept for the next time).</summary>
    [RelayCommand]
    private void CloseSearch()
    {
        IsSearchOpen = false;
        StopSearch();
        SearchHits = [];
        CurrentSearchHit = null;
    }

    /// <summary>F3, Enter in the search box: the next hit (after the last one the first again); opens the bar if nothing is searched yet.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void FindNext() => Step(+1);

    /// <summary>Shift+F3, Shift+Enter in the search box: the previous hit (before the first one the last again).</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void FindPrevious() => Step(-1);

    private void Step(int direction)
    {
        if (!IsSearchOpen || TextSearch.NormalizeQuery(SearchText).Length == 0)
        {
            ShowSearch();
            return;
        }
        if (SearchHits.Count == 0)
            return;
        var index = CurrentSearchHit is { } hit ? IndexOfHit(hit) : -1;
        index = index < 0
            ? (direction > 0 ? 0 : SearchHits.Count - 1)
            : (index + direction + SearchHits.Count) % SearchHits.Count;
        ShowHit(SearchHits[index]);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (IsSearchOpen)
            RestartSearch(navigate: true);
    }

    /// <summary>Shows a hit: the reading view (hits are highlighted there) at its page, scrolled so that it is visible.</summary>
    private void ShowHit(TextSearchHit hit)
    {
        CurrentSearchHit = hit;
        if (Document == null || hit.PageIndex >= Document.PageCount)
            return;
        CurrentPage = hit.PageIndex;
        IsOrganizeMode = false;
        SearchHitShown?.Invoke(hit);
    }

    private void StopSearch()
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
        IsSearching = false;
    }

    /// <summary>The document changed: its hits are searched again (without moving the view).</summary>
    private void RefreshSearch()
    {
        if (IsSearchOpen)
            RestartSearch(navigate: false);
    }

    /// <summary>
    /// Searches the document anew. The first hit from the current page on (or from the page of the current hit) becomes
    /// the current one; with <paramref name="navigate"/> the view goes there.
    /// </summary>
    private void RestartSearch(bool navigate)
    {
        var startPage = CurrentSearchHit?.PageIndex ?? CurrentPage;
        StopSearch();
        SearchHits = [];
        CurrentSearchHit = null;
        _searchFoundNoText = false;
        var query = TextSearch.NormalizeQuery(SearchText);
        if (query.Length == 0 || Document == null)
        {
            OnPropertyChanged(nameof(SearchStatusText));
            return;
        }
        _search = new CancellationTokenSource();
        _ = SearchAsync(Document.Pages, query, Math.Clamp(startPage, 0, Math.Max(0, Document.PageCount - 1)), navigate, _search.Token);
    }

    private async Task SearchAsync(IReadOnlyList<PageEntry> pages, string query, int startPage, bool navigate, CancellationToken token)
    {
        IsSearching = true;
        var hits = new List<TextSearchHit>();
        var anyText = false;
        try
        {
            // From the start page to the end first, so the hit to show comes soon; then the pages before it
            foreach (var index in Enumerable.Range(startPage, pages.Count - startPage).Concat(Enumerable.Range(0, startPage)))
            {
                var page = pages[index];
                var words = await WordsForSearchAsync(page, token); // continues on the UI thread
                token.ThrowIfCancellationRequested();
                anyText |= words.Count > 0;
                var found = TextSearch.Find(words, query);
                if (found.Count == 0)
                    continue;
                // Keep document order: the pages before the start page go in front of the start page's hits
                var insertAt = index < startPage ? hits.FindIndex(h => h.PageIndex >= startPage) is >= 0 and var at ? at : hits.Count : hits.Count;
                hits.InsertRange(insertAt, found.Select(boxes => new TextSearchHit(index, page, boxes)));
                SearchHits = [.. hits];
                if (CurrentSearchHit == null)
                {
                    if (navigate)
                        ShowHit(hits[insertAt]);
                    else
                        CurrentSearchHit = hits[insertAt];
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _searchFoundNoText = !anyText;
        IsSearching = false;
        OnPropertyChanged(nameof(SearchStatusText));
    }

    private async Task<IReadOnlyList<OcrWord>> WordsForSearchAsync(PageEntry page, CancellationToken token)
    {
        if (_searchWords.TryGetValue(page, out var known))
            return known;
        IReadOnlyList<OcrWord> words;
        try
        {
            words = await PageText.ReadWordsAsync(page, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            words = []; // closed or broken page: no text
        }
        _searchWords.AddOrUpdate(page, words);
        return words;
    }
}
