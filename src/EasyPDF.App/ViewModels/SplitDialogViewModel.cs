using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.Core.Documents;

namespace EasyPDF.App.ViewModels;

public partial class SplitDialogViewModel : ObservableObject
{
    private readonly SplitDialogContext _context;

    [ObservableProperty]
    private bool _everyN = true;

    [ObservableProperty]
    private string _pagesPerPart = "10";

    [ObservableProperty]
    private string _folder;

    [ObservableProperty]
    private string _baseName;

    public SplitDialogViewModel(SplitDialogContext context)
    {
        _context = context;
        _folder = context.DefaultFolder;
        _baseName = context.BaseName;
        if (context.SelectedPages is { Count: > 0 })
            _everyN = false;
    }

    public bool CanSplitAtSelected => _context.SelectedPages is { Count: > 0 };

    public bool AtSelected
    {
        get => !EveryN;
        set => EveryN = !value;
    }

    partial void OnEveryNChanged(bool value)
    {
        OnPropertyChanged(nameof(AtSelected));
        Revalidate();
    }

    partial void OnPagesPerPartChanged(string value) => Revalidate();

    partial void OnFolderChanged(string value) => Revalidate();

    partial void OnBaseNameChanged(string value) => Revalidate();

    private void Revalidate()
    {
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(IsValid));
    }

    /// <summary>The parts to write; null if the page count per part is invalid.</summary>
    private IReadOnlyList<SplitPart>? Plan()
    {
        if (EveryN)
            return int.TryParse(PagesPerPart?.Trim(), out var n) && n >= 1 ? SplitPlan.EveryN(_context.PageCount, n) : null;
        return SplitPlan.AtPages(_context.PageCount, _context.SelectedPages);
    }

    /// <summary>Localized reason why OK is disabled; empty if the input is fine.</summary>
    public string ErrorText
    {
        get
        {
            var plan = Plan();
            if (plan == null)
                return Loc.T("ErrSplitCount");
            if (string.IsNullOrWhiteSpace(Folder) || !Directory.Exists(Folder))
                return Loc.T("ErrSplitFolder");
            if (string.IsNullOrWhiteSpace(BaseName) || BaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return Loc.T("ErrSplitName");
            var existing = Enumerable.Range(0, plan.Count)
                .Select(i => PdfSaver.PartFileName(BaseName.Trim(), i, plan.Count))
                .Where(name => File.Exists(Path.Combine(Folder, name)))
                .ToList();
            return existing.Count > 0 ? Loc.F("ErrSplitExists", existing[0], existing.Count) : "";
        }
    }

    public bool IsValid => ErrorText.Length == 0;

    /// <summary>Number of files that will be written (0 if the input is invalid).</summary>
    public int PartCount => Plan()?.Count ?? 0;

    public SplitRequest? BuildRequest() => IsValid && Plan() is { } plan ? new SplitRequest(plan, Folder, BaseName.Trim()) : null;
}
