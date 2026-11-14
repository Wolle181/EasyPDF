using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PDFiumCore;

namespace EasyPDF.Core.Pdfium;

/// <summary>
/// Runs ALL pdfium calls on one dedicated thread (pdfium is not thread-safe).
/// Jobs are ordered by priority (lower number = more urgent), then FIFO; queued jobs can be cancelled.
/// </summary>
public sealed class PdfiumWorker
{
    public const int PriorityVisible = 0;
    public const int PriorityPrefetch = 10;
    public const int PriorityThumbnail = 20;
    public const int PriorityBackground = 50;

    /// <summary>pdfium is initialised once per process, so there is exactly one worker.</summary>
    public static PdfiumWorker Shared { get; } = new();

    private abstract class Job
    {
        public long Seq;
        public int Priority;
        public abstract void Execute();
        public abstract void Cancel();
    }

    private sealed class Job<T>(Func<T> work) : Job
    {
        public readonly Func<T> Work = work;
        public CancellationToken Token;
        public readonly TaskCompletionSource<T> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;

        public override void Execute()
        {
            Registration.Dispose();
            if (Token.IsCancellationRequested)
            {
                Completion.TrySetCanceled(Token);
                return;
            }
            try { Completion.TrySetResult(Work()); }
            catch (Exception ex) { Completion.TrySetException(ex); }
        }

        public override void Cancel() => Completion.TrySetCanceled(Token);
    }

    private sealed class JobComparer : IComparer<Job>
    {
        public int Compare(Job? a, Job? b)
        {
            if (a == null || b == null)
                return a == b ? 0 : a == null ? -1 : 1;
            var c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }
    }

    private readonly SortedSet<Job> _queue = new(new JobComparer());
    private readonly object _lock = new();
    private long _seq;

    private PdfiumWorker()
    {
        var thread = new Thread(Loop) { IsBackground = true, Name = "pdfium" };
        thread.Start();
    }

    private void Loop()
    {
        fpdfview.FPDF_InitLibrary();
        while (true)
        {
            Job job;
            lock (_lock)
            {
                while (_queue.Count == 0)
                    Monitor.Wait(_lock);
                job = _queue.Min!;
                _queue.Remove(job);
            }
            job.Execute();
        }
    }

    public Task<T> Run<T>(Func<T> work, int priority = PriorityBackground, CancellationToken token = default)
    {
        if (token.IsCancellationRequested)
            return Task.FromCanceled<T>(token);

        var job = new Job<T>(work) { Priority = priority, Token = token };
        lock (_lock)
        {
            job.Seq = ++_seq;
            _queue.Add(job);
            Monitor.Pulse(_lock);
        }
        if (token.CanBeCanceled)
        {
            job.Registration = token.Register(() =>
            {
                bool removed;
                lock (_lock) removed = _queue.Remove(job);
                if (removed)
                    job.Cancel();
            });
        }
        return job.Completion.Task;
    }

    public Task Run(Action work, int priority = PriorityBackground, CancellationToken token = default) =>
        Run<object?>(() => { work(); return null; }, priority, token);
}
