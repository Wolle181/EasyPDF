using EasyPDF.App.Localization;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.ViewModels;

internal static class RangeErrors
{
    /// <summary>Localized explanation of a failed page range parse; empty if it succeeded.</summary>
    public static string Text(PageRangeResult r) => r.Error switch
    {
        PageRangeError.Empty => Loc.T("ErrRangeEmpty"),
        PageRangeError.Syntax => Loc.F("ErrRangeSyntax", r.Token),
        PageRangeError.OutOfRange => Loc.F("ErrRangeOutOfRange", r.Token, Loc.Pages(r.PageCount)),
        PageRangeError.ReversedRange => Loc.F("ErrRangeReversed", r.Token),
        _ => "",
    };
}
