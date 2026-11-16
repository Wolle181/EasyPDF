using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

public class AttachmentTests
{
    private static readonly byte[] Invoice = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><rsm:CrossIndustryInvoice/>");
    private static readonly byte[] Notes = Encoding.UTF8.GetBytes("some notes");

    [Fact]
    public async Task ListsAndReadsEmbeddedFiles()
    {
        using var dir = new TempDir();
        var path = dir.File("invoice.pdf");
        TestPdf.WithAttachments(path, ("notes.txt", Notes), ("factur-x.xml", Invoice));
        await using var source = await PdfSource.OpenAsync(path);

        var attachments = await source.ListAttachmentsAsync();

        Assert.Equal(["notes.txt", "factur-x.xml"], attachments.Select(a => a.Name));
        Assert.Equal([Notes.Length, Invoice.Length], attachments.Select(a => a.Size));
        Assert.Equal([false, true], attachments.Select(a => a.IsEInvoice));
        Assert.Equal(Invoice, await source.ReadAttachmentAsync(attachments[1].Index));
        Assert.Equal(Notes, await source.ReadAttachmentAsync(attachments[0].Index));
    }

    [Fact]
    public async Task SavingKeepsTheAttachments()
    {
        using var dir = new TempDir();
        var path = dir.File("invoice.pdf");
        TestPdf.WithAttachments(path, ("factur-x.xml", Invoice));
        var target = dir.File("saved.pdf");
        await using (var document = EditableDocument.FromSource(await PdfSource.OpenAsync(path)))
        {
            document.Rotate([0], 90);
            await document.SaveAsync(target);
        }

        await using var saved = await PdfSource.OpenAsync(target);
        var attachment = Assert.Single(await saved.ListAttachmentsAsync());
        Assert.True(attachment.IsEInvoice);
        Assert.Equal(Invoice, await saved.ReadAttachmentAsync(attachment.Index));
    }

    [Fact]
    public async Task FileWithoutAttachmentsHasNone()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "plain.pdf", 2);
        await using var source = await PdfSource.OpenAsync(path);

        Assert.Empty(await source.ListAttachmentsAsync());
    }

    [Theory]
    [InlineData("factur-x.xml", true)]
    [InlineData("ZUGFeRD-invoice.xml", true)]
    [InlineData("zugferd-invoice.xml", true)]
    [InlineData("xrechnung.xml", true)]
    [InlineData("invoice.xml", false)]
    [InlineData("factur-x.xml.exe", false)]
    public void RecognizesElectronicInvoices(string name, bool expected) =>
        Assert.Equal(expected, new PdfAttachment(0, name, 1).IsEInvoice);

    [Theory]
    [InlineData("factur-x.xml", "factur-x.xml")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\evil.txt", "evil.txt")]
    [InlineData("a:b?c.txt", "a_b_c.txt")]
    [InlineData("..", "attachment")]
    [InlineData("", "attachment")]
    [InlineData("name. ", "name")]
    public void FileNamesAreSafe(string name, string expected) =>
        Assert.Equal(expected, new PdfAttachment(0, name, 1).SafeFileName);

    [Theory]
    [InlineData("factur-x.xml", true)]
    [InlineData("scan.PDF", true)]
    [InlineData("setup.exe", false)]
    [InlineData("script.js", false)]
    [InlineData("macro.docm", false)]
    [InlineData("noextension", false)]
    public void OnlyHarmlessFilesOpenInTheSystemProgram(string name, bool expected) =>
        Assert.Equal(expected, new PdfAttachment(0, name, 1).CanOpen);
}
