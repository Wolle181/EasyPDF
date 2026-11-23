using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Fonts;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

/// <summary>New text in the font and size of the page's text: reading font files, matching installed fonts, embedding.</summary>
public class FontTests
{
    /// <summary>The program's own font files (Barlow, TrueType, SIL OFL: may be embedded) stand in for installed fonts.</summary>
    private static string BarlowFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var folder = Path.Combine(dir.FullName, "src", "EasyPDF.UI", "Assets", "Fonts");
            if (Directory.Exists(folder))
                return folder;
        }
        throw new DirectoryNotFoundException("src/EasyPDF.UI/Assets/Fonts");
    }

    private static async Task<EditableDocument> OpenAsync(TempDir dir)
    {
        var path = Path.Combine(dir.Path, "a.pdf");
        TestPdf.Create(path, [(400, 300, 0), (400, 300, 0)]);
        var source = await PdfSource.OpenAsync(path);
        await source.LoadPageSizesAsync();
        return EditableDocument.FromSource(source);
    }

    private static async Task<PageFont?> MainFontAsync(string path, int page)
    {
        await using var source = await PdfSource.OpenAsync(path);
        return await source.ReadMainFontAsync(page);
    }

    [Fact]
    public void AFontFileTellsFamilyStylePostScriptNameAndAscent()
    {
        var face = FontFile.Read(Path.Combine(BarlowFolder(), "Barlow-Bold.ttf"))!;
        Assert.Equal("Barlow", face.Family);
        Assert.Equal("Barlow-Bold", face.PostScriptName);
        Assert.True(face.Bold);
        Assert.False(face.Italic);
        Assert.InRange(face.Ascent, 0.6, 0.95);
        Assert.Null(FontFile.Read(Path.Combine(BarlowFolder(), "missing.ttf")));
    }

    [Fact]
    public void InstalledFontsAreFoundByPostScriptNameOrFamilyAndStyle()
    {
        var fonts = new FontLibrary([BarlowFolder()]);
        Assert.Equal("Barlow-SemiBold", fonts.Match("ABCDEF+Barlow-SemiBold", "", true, false)!.PostScriptName);
        Assert.Equal("Barlow-Bold", fonts.Match("Barlow,Bold", "", true, false)!.PostScriptName);
        Assert.Equal("Barlow-Regular", fonts.Match("BarlowXYZ", "Barlow", false, false)!.PostScriptName);
        Assert.Equal("Barlow-Bold", fonts.Find("barlow", true, false)!.PostScriptName);
        Assert.Null(fonts.Match("ArialMT", "Arial", false, false));
        Assert.Equal("timesnewroman", FontLibrary.Normalize("TimesNewRomanPSMT"));
        Assert.Equal("timesnewroman", FontLibrary.Normalize("Times New Roman"));
    }

    [Fact]
    public void AFontThatIsNotInstalledBecomesTheMostSimilarStandardFont()
    {
        var fonts = new FontLibrary([BarlowFolder()]);
        Assert.Equal(new TextPen("Times", 11, true, false), new PageFont("Times-Bold", "Times", 11, true, false, true, false).ToPen(fonts));
        Assert.Equal(new TextPen("Times", 10.5), new PageFont("Georgia", "Georgia", 10.4, false, false, false, false).ToPen(fonts));
        Assert.Equal(new TextPen("Courier", 9), new PageFont("Consolas", "", 9, false, false, false, true).ToPen(fonts));
        Assert.Equal(new TextPen("Helvetica", 200, false, true), new PageFont("Verdana-Italic", "", 500, false, true, false, false).ToPen(fonts));
        Assert.Equal(new TextPen("Barlow", 12, true), new PageFont("Barlow-Bold", "Barlow", 12, true, false, false, false).ToPen(fonts));
        Assert.Equal(new TextPen("Helvetica", 12, true), new PageFont("Barlow-Bold", "Barlow", 12, true, false, false, false).ToPen(null));
    }

    [Fact]
    public void SubsetPrefixAndStyleComeFromTheName()
    {
        Assert.Equal("Arial-BoldMT", PageFont.WithoutSubsetPrefix("ABCDEF+Arial-BoldMT"));
        Assert.Equal("Abcdef+X", PageFont.WithoutSubsetPrefix("Abcdef+X"));
        Assert.Equal((true, true), PageFont.StyleFromName("Arial-BoldItalicMT"));
        Assert.Equal((true, false), PageFont.StyleFromName("Calibri,Bold"));
        Assert.Equal((false, true), PageFont.StyleFromName("Helvetica-Oblique"));
        Assert.Equal((false, false), PageFont.StyleFromName("BoldFace"));     // only the part after "-" or "," counts
    }

    [Fact]
    public async Task TheMainFontOfAPageIsTheOneMostCharactersAreWrittenIn()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.AddText(0, "Body text in Times, bold and large enough", "Times", 14, "#000000", new RectPt(20, 20, 360, 20), bold: true);
        d.AddText(0, "small", "Helvetica", 8, "#000000", new RectPt(20, 60, 40, 10));
        var path = dir.File("out.pdf");
        await d.SaveAsync(path);

        var font = (await MainFontAsync(path, 0))!;
        Assert.Equal("Times-Bold", font.BaseFont);
        Assert.Equal(14, font.Size);
        Assert.True(font.Bold);
        Assert.Equal(new TextPen("Times", 14, true), font.ToPen(new FontLibrary([])));
        Assert.Null(await MainFontAsync(path, 1));                // no text
    }

    [Fact]
    public async Task InvisibleTextOfAScanDoesNotCount()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.SetOcrWords(new System.Collections.Generic.Dictionary<int, System.Collections.Generic.IReadOnlyList<Ocr.OcrWord>> { [d.Pages[0].Id] = [new Ocr.OcrWord("Invisible", new RectPt(20, 20, 100, 14), 1)] });
        var path = dir.File("out.pdf");
        await d.SaveAsync(path);
        Assert.Null(await MainFontAsync(path, 0));
    }

    [Fact]
    public async Task TextInAnInstalledFontIsEmbeddedAndReadsBackInThatFont()
    {
        FontLibrary.Shared.AddFolder(BarlowFolder());
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.AddText(0, "Grüße aus Barlow – 10 €", "Barlow", 16, "#000000", new RectPt(20, 20, 300, 24), bold: true);
        var path = dir.File("out.pdf");
        await d.SaveAsync(path);

        var font = (await MainFontAsync(path, 0))!;
        Assert.Contains("Barlow", font.BaseFont);
        Assert.Equal(16, font.Size);
        Assert.Equal(new TextPen("Barlow", 16, true), font.ToPen(FontLibrary.Shared));
        await using var source = await PdfSource.OpenAsync(path);
        var words = await source.ReadWordsAsync(0);
        Assert.Equal("Grüße aus Barlow – 10 €", string.Join(" ", words.Select(w => w.Text)));
    }

    [Fact]
    public async Task AnInstalledFontMissingWhenSavingIsWrittenAsTheMostSimilarStandardFont()
    {
        using var dir = new TempDir();
        await using var d = await OpenAsync(dir);
        d.AddText(0, "Written elsewhere in a serif font", "Some Serif Font", 12, "#000000", new RectPt(20, 20, 300, 18), italic: true);
        var path = dir.File("out.pdf");
        await d.SaveAsync(path);
        Assert.Equal("Times-Italic", (await MainFontAsync(path, 0))!.BaseFont);
    }
}
