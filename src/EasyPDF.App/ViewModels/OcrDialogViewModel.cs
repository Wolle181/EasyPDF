using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.ViewModels;

/// <summary>"Recognize text" dialog: which pages, and whether pages that already have text are left out.</summary>
public partial class OcrDialogViewModel(OcrDialogContext context) : ObservableObject
{
    // Usually a whole scan is recognized; a selection narrows it down (one selected page is the current page)
    [ObservableProperty]
    private RotateScope _scope = context.SelectedPages.Count switch { 0 => RotateScope.All, 1 => RotateScope.Current, _ => RotateScope.Selected };

    [ObservableProperty]
    private string _rangeText = "";

    [ObservableProperty]
    private bool _skipPagesWithText = true;

    public bool CanSelectSelected => context.HasSelection;

    public string CurrentPageText => Loc.F("RotateScopeCurrent", context.CurrentPage + 1);

    public bool IsAll { get => Scope == RotateScope.All; set { if (value) Scope = RotateScope.All; } }

    public bool IsCurrent { get => Scope == RotateScope.Current; set { if (value) Scope = RotateScope.Current; } }

    public bool IsSelected { get => Scope == RotateScope.Selected; set { if (value) Scope = RotateScope.Selected; } }

    public bool IsRangeScope { get => Scope == RotateScope.Range; set { if (value) Scope = RotateScope.Range; } }

    partial void OnScopeChanged(RotateScope value)
    {
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsRangeScope));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ErrorText));
    }

    partial void OnRangeTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ErrorText));
    }

    private PageRangeResult ParseRange() => PageRangeParser.Parse(RangeText, context.PageCount);

    /// <summary>OK is enabled when the pages are well defined.</summary>
    public bool IsValid => Scope != RotateScope.Range || ParseRange().Success;

    /// <summary>Localized explanation of an invalid range; empty if fine or the range is not in use.</summary>
    public string ErrorText => Scope == RotateScope.Range ? RangeErrors.Text(ParseRange()) : "";

    /// <summary>The result for OK; null if the input is invalid.</summary>
    public OcrRequest? BuildRequest()
    {
        if (!IsValid)
            return null;
        IReadOnlyList<int> pages = Scope switch
        {
            RotateScope.All => [.. Enumerable.Range(0, context.PageCount)],
            RotateScope.Range => ParseRange().Pages,
            RotateScope.Selected => context.SelectedPages,
            _ => [context.CurrentPage],
        };
        return new OcrRequest(pages, SkipPagesWithText);
    }
}
