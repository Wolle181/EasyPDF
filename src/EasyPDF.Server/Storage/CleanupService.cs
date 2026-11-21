using System;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Server.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EasyPDF.Server.Storage;

/// <summary>Deletes the leftovers of an earlier run at start-up, then regularly the expired files and anonymous sessions.</summary>
public sealed class CleanupService(FileStore files, OpenDocuments documents, SessionStore sessions, IOptions<ServerOptions> options,
    TimeProvider time) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        files.DeleteLeftovers();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.Storage.CleanupIntervalSeconds));
        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync();
    }

    /// <summary>One pass (also called by the tests).</summary>
    public async Task RunOnceAsync()
    {
        foreach (var file in files.RemoveExpired())
            await documents.ReleaseAsync(file);
        var idle = TimeSpan.FromMinutes(Math.Max(1, options.Value.Authentication.SessionIdleMinutes));
        foreach (var owner in sessions.RemoveExpired(idle))
        {
            foreach (var file in files.RemoveOwner(owner))
                await documents.ReleaseAsync(file);
            files.DeleteOwnerFolder(owner); // again, now that its files are closed (Windows)
        }
    }
}
