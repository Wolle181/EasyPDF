using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.App.Views;
using EasyPDF.Core.Pdfium;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>"Send by email": the document goes to the email program as an attachment.</summary>
public class MailTests
{
    private sealed class FakeMail : IMailService
    {
        public readonly List<(string File, string Subject, int Pages)> Sent = [];
        public Exception? Failure;
        public TaskCompletionSource? Hold;

        public async Task SendAsync(string filePath, string subject)
        {
            if (Failure != null)
                throw Failure;
            await using (var source = await PdfSource.OpenAsync(filePath))
                Sent.Add((filePath, subject, source.PageCount));
            if (Hold != null)
                await Hold.Task; // like Outlook: returns when the message window closes
        }
    }

    [Fact]
    public async Task AnUnchangedDocumentIsAttachedAsItsOwnFile()
    {
        using var dir = new TempDir();
        var mail = new FakeMail();
        var vm = new MainViewModel(new AppSettings(), new FakeDialogs(), mail: mail);
        Assert.False(vm.SendByMailCommand.CanExecute(null));
        var path = dir.Pdf("report.pdf", 3);
        await vm.OpenPathAsync(path);
        Assert.True(vm.SendByMailCommand.CanExecute(null));

        await vm.SendByMailCommand.ExecuteAsync(null);
        Assert.Equal([(path, "report.pdf", 3)], mail.Sent);
        Assert.False(vm.IsBusy);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task UnsavedChangesAreSentAsACopyWithTheDocumentsName()
    {
        using var dir = new TempDir();
        var mail = new FakeMail();
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new AppSettings(), dialogs, mail: mail);
        var path = dir.Pdf("report.pdf", 4);
        await vm.OpenPathAsync(path);
        vm.Document!.Delete([0]);

        await vm.SendByMailCommand.ExecuteAsync(null);
        var (file, subject, pages) = Assert.Single(mail.Sent);
        Assert.NotEqual(Path.GetFullPath(path), Path.GetFullPath(file));
        Assert.Equal("report.pdf", Path.GetFileName(file));
        Assert.Equal("report.pdf", subject);
        Assert.Equal(3, pages);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp", SearchOption.AllDirectories));
        Assert.True(vm.Document.IsDirty); // sending does not save
        Assert.Empty(dialogs.Errors);
        Assert.False(vm.IsBusy);
        await vm.Document.DisposeAsync();
        await using (var original = await PdfSource.OpenAsync(path, null, TestContext.Current.CancellationToken))
            Assert.Equal(4, original.PageCount);
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
    }

    [Fact]
    public async Task NoEmailProgramShowsAnError()
    {
        using var dir = new TempDir();
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(new AppSettings(), dialogs, mail: new FakeMail { Failure = new InvalidOperationException("MAPI error 2") });
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 1));

        await vm.SendByMailCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Errors);
        Assert.False(vm.IsBusy);
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public async Task TheAppStaysUsableWhileTheMessageWindowIsOpen()
    {
        using var dir = new TempDir();
        var mail = new FakeMail { Hold = new TaskCompletionSource() };
        var vm = new MainViewModel(new AppSettings(), new FakeDialogs(), mail: mail);
        await vm.OpenPathAsync(dir.Pdf("a.pdf", 2));

        var first = vm.SendByMailCommand.ExecuteAsync(null);
        while (mail.Sent.Count == 0)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);
        Assert.False(vm.IsBusy);
        Assert.True(vm.SendByMailCommand.CanExecute(null)); // a second email can be started
        Assert.True(vm.SaveAsCommand.CanExecute(null));

        mail.Hold.SetResult();
        await first;
        await vm.Document!.DisposeAsync();
    }

    [Fact]
    public void ThunderbirdGetsTheAttachmentAsAQuotedFileUrl()
    {
        var path = OperatingSystem.IsWindows() ? @"C:\Docs\O'Brien, offer.pdf" : "/home/a/O'Brien, offer.pdf";
        var url = OperatingSystem.IsWindows() ? "file:///C:/Docs/O%27Brien,%20offer.pdf" : "file:///home/a/O%27Brien,%20offer.pdf";
        Assert.Equal($"subject='OBrien, offer.pdf',attachment='{url}'", PlatformMailService.ThunderbirdCompose(path, "O'Brien, offer.pdf"));
    }

    [AvaloniaFact]
    public async Task TheRibbonAndTheFileMenuOfferTheCommand()
    {
        var window = new MainWindow { Width = 1100, Height = 700 };
        window.Show();
        Assert.Contains(window.LogicalDescendantsOfType<RibbonButton>(), b => b.Command == window.ViewModel.SendByMailCommand);
        var file = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().First().Menu!;
        Assert.Contains(file.Items.OfType<NativeMenuItem>(), i => i.Command == window.ViewModel.SendByMailCommand);
        Assert.False(window.ViewModel.SendByMailCommand.CanExecute(null));

        using var dir = new TempDir();
        await window.OpenPathAsync(dir.Pdf("a.pdf", 2));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.ViewModel.SendByMailCommand.CanExecute(null));
        window.Close();
        await window.ViewModel.ReleaseDocumentAsync();
    }

    /// <summary>The web version's files: records what is prepared and shared, answers as the browser would.</summary>
    private sealed class FakeShareFiles(params ShareOutcome[] outcomes) : IPlatformFiles
    {
        private readonly Queue<ShareOutcome> _outcomes = new(outcomes);
        public bool CanShareFiles { get; init; } = true;
        public readonly List<(int Pages, string Name)> Prepared = [];
        public readonly List<string> Shared = [];

        public Task<string> PrepareForSharingAsync(EasyPDF.Core.Documents.EditableDocument document, string name)
        {
            Prepared.Add((document.PageCount, name));
            return Task.FromResult("api/files/abc/content");
        }

        public Task<ShareOutcome> ShareAsync(string prepared, string name)
        {
            Shared.Add(prepared);
            return Task.FromResult(_outcomes.Dequeue());
        }

        public Task<string?> ToOpenPathAsync(Avalonia.Platform.Storage.IStorageFile file) => throw new NotSupportedException();
        public string? ToSavePath(Avalonia.Platform.Storage.IStorageFile file) => throw new NotSupportedException();
        public Task WriteAsync(string path, byte[] content) => throw new NotSupportedException();
        public Task<bool> ShowForPrintingAsync(IReadOnlyList<EasyPDF.Core.Documents.PageEntry> pages, string name) => throw new NotSupportedException();
        public Task<bool> ShowXmlAttachmentAsync(EasyPDF.Core.Pdfium.IPdfSource source, PdfAttachment attachment) => throw new NotSupportedException();
    }

    private static async Task<(MainViewModel Vm, FakeDialogs Dialogs, FakeMail Mail)> WebDocumentAsync(TempDir dir, IPlatformFiles files)
    {
        PlatformFeatures.Current = PlatformFeatures.Web((_, _) => true);
        PlatformFeatures.Files = files;
        var dialogs = new FakeDialogs();
        var mail = new FakeMail();
        var vm = new MainViewModel(new AppSettings(), dialogs, mail: mail);
        await vm.OpenPathAsync(dir.Pdf("report.pdf", 3));
        vm.Document!.Delete([0]);
        return (vm, dialogs, mail);
    }

    private static void RestoreDesktop()
    {
        PlatformFeatures.Current = PlatformFeatures.Desktop;
        PlatformFeatures.Files = new LocalPlatformFiles();
    }

    [Fact]
    public async Task OnTheWebTheDocumentGoesToTheSystemsShareSheet()
    {
        using var dir = new TempDir();
        var files = new FakeShareFiles(ShareOutcome.Shared);
        try
        {
            var (vm, dialogs, mail) = await WebDocumentAsync(dir, files);
            await vm.SendByMailCommand.ExecuteAsync(null);
            Assert.Equal([(2, "report.pdf")], files.Prepared);   // as it is now, under the document's name
            Assert.Single(files.Shared);
            Assert.Empty(mail.Sent);                                 // not the desktop's email program
            Assert.Empty(dialogs.Infos);
            Assert.Empty(dialogs.Errors);
            Assert.Equal(EasyPDF.App.Localization.Loc.F("MsgShared", "report.pdf"), vm.StatusMessage);
            Assert.False(vm.IsBusy);
            await vm.Document!.DisposeAsync();
        }
        finally
        {
            RestoreDesktop();
        }
    }

    [Fact]
    public async Task OnTheWebASlowPreparationAsksForOneMoreClick()
    {
        using var dir = new TempDir();
        var files = new FakeShareFiles(ShareOutcome.NeedsClick, ShareOutcome.Shared);
        try
        {
            var (vm, dialogs, _) = await WebDocumentAsync(dir, files);
            await vm.SendByMailCommand.ExecuteAsync(null);
            Assert.Single(files.Prepared);                           // prepared once, shared after the click
            Assert.Equal(2, files.Shared.Count);
            Assert.Equal([EasyPDF.App.Localization.Loc.F("MsgShareReady", "report.pdf")], dialogs.Infos);
            Assert.Empty(dialogs.Errors);
            await vm.Document!.DisposeAsync();
        }
        finally
        {
            RestoreDesktop();
        }
    }

    [Fact]
    public async Task ABrowserWithoutShareSheetOffersTheDownload()
    {
        using var dir = new TempDir();
        var files = new FakeShareFiles { CanShareFiles = false };
        try
        {
            var (vm, dialogs, _) = await WebDocumentAsync(dir, files);
            await vm.SendByMailCommand.ExecuteAsync(null);
            Assert.Equal([EasyPDF.App.Localization.Loc.T("MsgShareUnavailable")], dialogs.Infos);
            Assert.Equal("report.pdf", dialogs.SuggestedFileName); // Save as (download) follows
            Assert.Empty(files.Prepared);
            Assert.Empty(dialogs.Errors);
            await vm.Document!.DisposeAsync();
        }
        finally
        {
            RestoreDesktop();
        }
    }

    [Fact]
    public async Task OnTheWebClosingTheShareSheetIsNoError()
    {
        using var dir = new TempDir();
        var files = new FakeShareFiles(ShareOutcome.Cancelled);
        try
        {
            var (vm, dialogs, _) = await WebDocumentAsync(dir, files);
            await vm.SendByMailCommand.ExecuteAsync(null);
            Assert.Empty(dialogs.Errors);
            Assert.Empty(dialogs.Infos);
            await vm.Document!.DisposeAsync();
        }
        finally
        {
            RestoreDesktop();
        }
    }
}
