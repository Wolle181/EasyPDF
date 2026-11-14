using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.ViewModels;

public partial class InsertRangeDialogViewModel(InsertRangeContext context) : ObservableObject
{
    [ObservableProperty]
    private bool _allPages = true;

    [ObservableProperty]
    private string _rangeText = "";

    public string Heading => Loc.F("InsertRangeHeading", context.FileName, Loc.Pages(context.PageCount));

    public bool IsRange
    {
        get => !AllPages;
        set => AllPages = !value;
    }

    partial void OnAllPagesChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRange));
        Revalidate();
    }

    partial void OnRangeTextChanged(string value) => Revalidate();

    private void Revalidate()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ErrorText));
    }

    private PageRangeResult ParseRange() => PageRangeParser.Parse(RangeText, context.PageCount);

    public bool IsValid => AllPages || ParseRange().Success;

    public string ErrorText => AllPages ? "" : RangeErrors.Text(ParseRange());

    /// <summary>Zero-based pages for OK; null if the input is invalid.</summary>
    public IReadOnlyList<int>? BuildResult() =>
        !IsValid ? null : AllPages ? [.. Enumerable.Range(0, context.PageCount)] : ParseRange().Pages;
}
