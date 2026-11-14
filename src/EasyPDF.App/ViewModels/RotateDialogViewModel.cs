using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.ViewModels;

public enum RotateScope
{
    All,
    Current,
    Selected,
    Range,
}

public partial class RotateDialogViewModel(RotateDialogContext context) : ObservableObject
{
    [ObservableProperty]
    private int _degrees = context.LastDegrees;

    [ObservableProperty]
    private RotateScope _scope = context.SelectedPages.Count > 1 ? RotateScope.Selected : RotateScope.Current;

    [ObservableProperty]
    private string _rangeText = "";

    public int PageCount => context.PageCount;

    public bool CanSelectSelected => context.HasSelection;

    public string CurrentPageText => Loc.F("RotateScopeCurrent", context.CurrentPage + 1);

    public bool IsClockwise { get => Degrees == 90; set { if (value) Degrees = 90; } }

    public bool IsCounterClockwise { get => Degrees == -90; set { if (value) Degrees = -90; } }

    public bool IsHalfTurn { get => Degrees == 180; set { if (value) Degrees = 180; } }

    public bool IsAll { get => Scope == RotateScope.All; set { if (value) Scope = RotateScope.All; } }

    public bool IsCurrent { get => Scope == RotateScope.Current; set { if (value) Scope = RotateScope.Current; } }

    public bool IsSelected { get => Scope == RotateScope.Selected; set { if (value) Scope = RotateScope.Selected; } }

    public bool IsRangeScope { get => Scope == RotateScope.Range; set { if (value) Scope = RotateScope.Range; } }

    partial void OnDegreesChanged(int value)
    {
        OnPropertyChanged(nameof(IsClockwise));
        OnPropertyChanged(nameof(IsCounterClockwise));
        OnPropertyChanged(nameof(IsHalfTurn));
    }

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
    public string ErrorText
    {
        get
        {
            if (Scope != RotateScope.Range)
                return "";
            return RangeErrors.Text(ParseRange());
        }
    }

    /// <summary>The result for OK; null if the input is invalid.</summary>
    public RotateResult? BuildResult()
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
        return new RotateResult(Degrees, pages);
    }
}
