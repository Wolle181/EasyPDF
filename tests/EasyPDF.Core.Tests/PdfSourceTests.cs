using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Pdfium;
using Xunit;

namespace EasyPDF.Core.Tests;

public class PdfSourceTests
{
    [Fact]
    public async Task OpensAndReadsPageSizes()
    {
        using var dir = new TempDir();
        var path = dir.File("a.pdf");
        TestPdf.Create(path, [(100, 200, 0), (300, 150, 0), (100, 200, 90)]);
        await using var s = await PdfSource.OpenAsync(path);
        Assert.Equal(3, s.PageCount);
        Assert.True(s.TryGetPageSize(0, out _));
        Assert.False(s.TryGetPageSize(1, out _)); // not loaded yet

        var progress = 0;
        await s.LoadPageSizesAsync(new Progress<int>(n => progress = n));
        Assert.Equal(new PageSizePt(300, 150), s.GetPageSizeOrEstimate(1));
        Assert.Equal(new PageSizePt(100, 200), s.GetPageSizeOrEstimate(0));
        // the page's own /Rotate is included in the size
        Assert.Equal(new PageSizePt(200, 100), s.GetPageSizeOrEstimate(2));
    }

    [Fact]
    public async Task RendersWhiteBackgroundWithRequestedSizeAndRotation()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "a.pdf", 1);
        await using var s = await PdfSource.OpenAsync(path);
        var img = await s.RenderAsync(0, 50, 100);
        Assert.Equal((50, 100), (img.Width, img.Height));
        Assert.Equal(img.Stride * 100, img.Pixels.Length);
        Assert.All(img.Pixels, b => Assert.Equal(255, b));

        var rotated = await s.RenderAsync(0, 100, 50, 90);
        Assert.Equal((100, 50), (rotated.Width, rotated.Height));
    }

    [Fact]
    public async Task SurvivesGarbageCollection()
    {
        // pdfium calls back into managed code to read the file; the delegate must not be collected
        using var dir = new TempDir();
        await using var s = await PdfSource.OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 3));
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.NotNull(await s.RenderAsync(i, 50, 100));
        }
    }

    [Fact]
    public async Task ReadsOutline()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "a.pdf", 3, outline: [("Intro", 0), ("Ende (\\344)", 2)]);
        await using var s = await PdfSource.OpenAsync(path);
        var outline = await s.ReadOutlineAsync();
        Assert.Equal(new[] { "Intro", "Ende (ä)" }, outline.Select(o => o.Title).ToArray());
        Assert.Equal(new[] { 0, 2 }, outline.Select(o => o.SourcePageIndex).ToArray());
    }

    [Fact]
    public async Task FileWithoutOutlineGivesEmptyList()
    {
        using var dir = new TempDir();
        await using var s = await PdfSource.OpenAsync(TestPdf.Numbered(dir.Path, "a.pdf", 2));
        Assert.Empty(await s.ReadOutlineAsync());
    }

    [Fact]
    public async Task InvalidFileThrowsOpenException()
    {
        using var dir = new TempDir();
        var bad = dir.File("bad.pdf");
        File.WriteAllText(bad, "this is not a pdf");
        var ex = await Assert.ThrowsAsync<PdfOpenException>(() => PdfSource.OpenAsync(bad));
        Assert.Equal(3UL, ex.ErrorCode);
        await Assert.ThrowsAsync<PdfOpenException>(() => PdfSource.OpenAsync(dir.File("missing.pdf")));
    }

    [Fact]
    public async Task CloseReleasesFileAndReopenWorks()
    {
        using var dir = new TempDir();
        var path = TestPdf.Numbered(dir.Path, "a.pdf", 2);
        var s = await PdfSource.OpenAsync(path);
        await s.CloseAsync();
        Assert.False(s.IsOpen);
        File.Delete(path); // would fail while the handle is open on Windows
        TestPdf.Numbered(dir.Path, "a.pdf", 4);
        await s.ReopenAsync();
        Assert.Equal(4, s.PageCount);
        await s.DisposeAsync();
    }

    [Fact]
    public async Task QueuedJobsRunByPriorityAndCanBeCancelled()
    {
        var worker = PdfiumWorker.Shared;
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var gate = new ManualResetEventSlim();
        var blocker = worker.Run(() => gate.Wait(), PdfiumWorker.PriorityVisible);
        var low = worker.Run(() => order.Enqueue(2), PdfiumWorker.PriorityBackground);
        var high = worker.Run(() => order.Enqueue(1), PdfiumWorker.PriorityPrefetch);
        using var cts = new CancellationTokenSource();
        var cancelled = worker.Run(() => order.Enqueue(3), PdfiumWorker.PriorityVisible, cts.Token);
        cts.Cancel();
        gate.Set();
        await Task.WhenAll(blocker, low, high);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(new[] { 1, 2 }, order.ToArray());
    }
}
