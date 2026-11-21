using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class AttachmentTests
{
    private static readonly byte[] Invoice = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><rsm:CrossIndustryInvoice/>");
    private static readonly byte[] Notes = Encoding.UTF8.GetBytes("some notes");
    private static readonly byte[] Program = [0x4D, 0x5A, 0x90, 0x00];

    private static async Task<(MainViewModel Vm, FakeDialogs Dialogs)> OpenAsync(string path)
    {
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new AppSettings(), dialogs);
        await vm.OpenPathAsync(path);
        await vm.AttachmentsLoading;
        return (vm, dialogs);
    }

    [AvaloniaFact]
    public async Task AnElectronicInvoiceIsAnnouncedAndCanBeSaved()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = await OpenAsync(dir.AttachmentPdf("Invoice 4711.pdf", ("notes.txt", Notes), ("factur-x.xml", Invoice)));

        Assert.True(vm.HasAttachments);
        Assert.Equal("factur-x.xml", vm.EInvoice?.Name);
        Assert.True(vm.EInvoiceBarVisible);
        Assert.True(vm.SaveEInvoiceCommand.CanExecute(null));

        dialogs.SaveFilePath = dir.File("out/invoice.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(dialogs.SaveFilePath)!);
        await vm.SaveEInvoiceCommand.ExecuteAsync(null);

        Assert.Equal("Invoice 4711.xml", dialogs.SuggestedSaveFileName); // named after the PDF, not factur-x.xml
        Assert.Equal(Invoice, File.ReadAllBytes(dialogs.SaveFilePath));
        Assert.Empty(dialogs.Errors);
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task TheInvoiceOpensInTheSystemProgram()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = await OpenAsync(dir.AttachmentPdf("a.pdf", ("factur-x.xml", Invoice)));

        await vm.OpenEInvoiceCommand.ExecuteAsync(null);

        var (path, content) = Assert.Single(dialogs.OpenedFiles);
        Assert.Equal("factur-x.xml", Path.GetFileName(path));
        Assert.Equal(Invoice, content);
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task TheBarCanBeClosedAndComesBackWithTheNextDocument()
    {
        using var dir = new TempDir();
        var (vm, _) = await OpenAsync(dir.AttachmentPdf("a.pdf", ("factur-x.xml", Invoice)));

        vm.DismissEInvoiceBarCommand.Execute(null);
        Assert.False(vm.EInvoiceBarVisible);
        Assert.True(vm.OpenEInvoiceCommand.CanExecute(null)); // still reachable through Attachments

        await vm.OpenPathAsync(dir.AttachmentPdf("b.pdf", ("xrechnung.xml", Invoice)));
        await vm.AttachmentsLoading;
        Assert.True(vm.EInvoiceBarVisible);
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task PlainPdfHasNoAttachmentsAndNoBar()
    {
        using var dir = new TempDir();
        var (vm, _) = await OpenAsync(dir.Pdf("plain.pdf", 2));

        Assert.False(vm.HasAttachments);
        Assert.False(vm.EInvoiceBarVisible);
        Assert.False(vm.ShowAttachmentsCommand.CanExecute(null));
        Assert.False(vm.SaveEInvoiceCommand.CanExecute(null));
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task OtherAttachmentsAreSavedFromTheList_ProgramsAreNeverOpened()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = await OpenAsync(dir.AttachmentPdf("a.pdf", ("notes.txt", Notes), ("setup.exe", Program)));
        Assert.Null(vm.EInvoice);
        Assert.False(vm.EInvoiceBarVisible);

        dialogs.AttachmentAnswer = list => new AttachmentChoice(list.Single(a => a.Name == "notes.txt"), AttachmentAction.Save);
        dialogs.SaveFilePath = dir.File("notes-copy.txt");
        await vm.ShowAttachmentsCommand.ExecuteAsync(null);
        Assert.Equal("notes.txt", dialogs.SuggestedSaveFileName);
        Assert.Equal(Notes, File.ReadAllBytes(dialogs.SaveFilePath));

        dialogs.AttachmentAnswer = list => new AttachmentChoice(list.Single(a => a.Name == "setup.exe"), AttachmentAction.Open);
        await vm.ShowAttachmentsCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.OpenedFiles);
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task AttachmentsStayAfterSaving()
    {
        using var dir = new TempDir();
        var (vm, dialogs) = await OpenAsync(dir.AttachmentPdf("a.pdf", ("factur-x.xml", Invoice)));
        vm.RotateRightCommand.Execute(0);
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.AttachmentsLoading;

        Assert.Equal("factur-x.xml", vm.EInvoice?.Name);
        dialogs.SaveFilePath = dir.File("after-save.xml");
        await vm.SaveEInvoiceCommand.ExecuteAsync(null);
        Assert.Equal(Invoice, File.ReadAllBytes(dialogs.SaveFilePath));
        await vm.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public async Task TheWindowShowsTheBarAndItsCloseButtonHidesIt()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var dir = new TempDir();
        await window.OpenPathAsync(dir.AttachmentPdf("a.pdf", ("factur-x.xml", Invoice)));
        await window.ViewModel.AttachmentsLoading;
        Dispatcher.UIThread.RunJobs();

        var bar = window.FindControl<Border>("EInvoiceBar")!;
        Assert.True(bar.IsVisible);
        Assert.True(window.FindControl<RibbonButton>("AttachmentsButton")!.IsEffectivelyVisible);

        var close = window.FindControl<RibbonButton>("EInvoiceCloseButton")!;
        var center = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.False(bar.IsVisible);
        window.Close();
        await window.ViewModel.ReleaseDocumentAsync();
    }

    [AvaloniaFact]
    public void TheDialogMarksTheInvoiceAndOffersOpenOnlyForHarmlessFiles()
    {
        var dialog = new AttachmentsDialog([new PdfAttachment(0, "setup.exe", 4), new PdfAttachment(1, "factur-x.xml", 2048)]);
        dialog.Show();
        var list = dialog.FindControl<ListBox>("AttachmentList")!;
        var open = dialog.FindControl<Button>("OpenButton")!;

        Assert.Equal("factur-x.xml", dialog.Selected?.Name); // the invoice is preselected
        Assert.Contains("factur-x.xml", (string)list.Items[1]!);
        Assert.NotEqual(AttachmentsDialog.Describe(new PdfAttachment(1, "factur-x.xml", 2048)),
            AttachmentsDialog.Describe(new PdfAttachment(1, "invoice.xml", 2048))); // the invoice is marked
        Assert.True(open.IsEnabled);

        list.SelectedIndex = 0;
        Assert.False(open.IsEnabled);
        Assert.True(dialog.FindControl<TextBlock>("CannotOpenText")!.IsVisible);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task OnTheWebTheInvoiceOpensInABrowserTabAndOtherFilesAreSaved()
    {
        using var dir = new TempDir();
        var files = new FakeWebFiles();
        PlatformFeatures.Current = PlatformFeatures.Web((_, _) => true);
        PlatformFeatures.Files = files;
        try
        {
            var (vm, dialogs) = await OpenAsync(dir.AttachmentPdf("a.pdf", ("notes.txt", Notes), ("factur-x.xml", Invoice)));

            await vm.OpenEInvoiceCommand.ExecuteAsync(null);
            Assert.Equal(["factur-x.xml"], files.Shown);
            Assert.Empty(dialogs.OpenedFiles); // not a program of the system
            Assert.Empty(dialogs.Errors);

            files.Blocked = true;
            await vm.OpenEInvoiceCommand.ExecuteAsync(null);
            Assert.Single(dialogs.Errors); // the browser blocked the tab: the user is told how to allow it
            await vm.ReleaseDocumentAsync();
        }
        finally
        {
            PlatformFeatures.Current = PlatformFeatures.Desktop;
            PlatformFeatures.Files = new LocalPlatformFiles();
        }
    }

    [AvaloniaFact]
    public void OnTheWebTheDialogOpensXmlFilesAndSavesTheOthers()
    {
        PlatformFeatures.Current = PlatformFeatures.Web((_, _) => true);
        try
        {
            var dialog = new AttachmentsDialog([new PdfAttachment(0, "photo.png", 4), new PdfAttachment(1, "factur-x.xml", 2048)]);
            dialog.Show();
            var list = dialog.FindControl<ListBox>("AttachmentList")!;
            var open = dialog.FindControl<Button>("OpenButton")!;
            var save = dialog.FindControl<Button>("SaveButton")!;

            Assert.True(open.IsVisible && open.IsEnabled && open.IsDefault); // the invoice: shown in a tab
            list.SelectedIndex = 0;
            Assert.False(open.IsVisible); // a browser cannot open a picture in a program of the system: only Save
            Assert.True(save.IsDefault);
            Assert.False(dialog.FindControl<TextBlock>("CannotOpenText")!.IsVisible);
            dialog.Close();
        }
        finally
        {
            PlatformFeatures.Current = PlatformFeatures.Desktop;
        }
    }

    /// <summary>The web version's files: records which XML attachments are shown in a tab.</summary>
    private sealed class FakeWebFiles : IPlatformFiles
    {
        public readonly System.Collections.Generic.List<string> Shown = [];
        public bool Blocked;

        public Task<bool> ShowXmlAttachmentAsync(IPdfSource source, PdfAttachment attachment)
        {
            if (!Blocked)
                Shown.Add(attachment.Name);
            return Task.FromResult(!Blocked);
        }

        public Task<string?> ToOpenPathAsync(Avalonia.Platform.Storage.IStorageFile file) => throw new System.NotSupportedException();
        public string? ToSavePath(Avalonia.Platform.Storage.IStorageFile file) => throw new System.NotSupportedException();
        public Task WriteAsync(string path, byte[] content) => throw new System.NotSupportedException();
        public Task<bool> ShowForPrintingAsync(System.Collections.Generic.IReadOnlyList<EasyPDF.Core.Documents.PageEntry> pages, string name) =>
            throw new System.NotSupportedException();
        public Task<string> PrepareForSharingAsync(EasyPDF.Core.Documents.EditableDocument document, string name) => throw new System.NotSupportedException();
        public Task<ShareOutcome> ShareAsync(string prepared, string name) => throw new System.NotSupportedException();
    }
}
