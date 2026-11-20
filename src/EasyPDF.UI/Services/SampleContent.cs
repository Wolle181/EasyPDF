using System.Collections.Generic;
using EasyPDF.App.Localization;
using EasyPDF.Core.Settings;
using EasyPDF.Core.Stamps;
using EasyPDF.Core.Templates;

namespace EasyPDF.App.Services;

/// <summary>
/// The samples that let the user try stamps and templates at once: at program start the stamp and templates folders are
/// created and get every sample not provided before (<see cref="AppSettings.ProvidedSamples"/>), also when the folders
/// exist already (e.g. kept from an uninstalled version).
/// </summary>
public static class SampleContent
{
    /// <summary>The sample templates, in the language the program shows at that moment; the id stays the same in every language.</summary>
    public static IReadOnlyList<(string Id, TextTemplate Template)> Templates() =>
    [
        ("Date", new() { Name = Loc.T("TemplateSampleDateName"), Text = Loc.T("TemplateSampleDateText") }),
        ("Page", new() { Name = Loc.T("TemplateSamplePageName"), Text = Loc.T("TemplateSamplePageText") }),
        ("Draft", new() { Name = Loc.T("TemplateSampleDraftName"), Text = Loc.T("TemplateSampleDraftText") }),
        ("Received", new() { Name = Loc.T("TemplateSampleReceivedName"), Text = Loc.T("TemplateSampleReceivedText") }),
    ];

    /// <summary>Name of a sample subfolder in the user's stamp folder, in the language the program shows ("Numbers" → "Nummern").</summary>
    public static string StampSubfolderName(string sampleSubfolder) =>
        sampleSubfolder == "Numbers" ? Loc.T("StampFolderNumbers") : sampleSubfolder;

    /// <summary>Creates both folders and adds the samples not provided yet; true if the settings changed (to be saved).</summary>
    public static bool Provide(AppSettings settings, string? stampSampleFolder = null)
    {
        var changed = StampLibrary.ProvideSamples(StampLibrary.ResolveFolder(settings), stampSampleFolder ?? StampLibrary.SampleFolder,
            settings.ProvidedSamples, StampSubfolderName);
        changed |= TemplateLibrary.ProvideSamples(TemplateLibrary.ResolveFolder(settings), Templates(), settings.ProvidedSamples);
        return changed;
    }
}
