using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.App.ViewModels;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

public class StatusMessageTests
{
    /// <summary>Replaces the message timer with delays the test finishes by hand.</summary>
    private static List<TaskCompletionSource> ControlTime(MainViewModel vm)
    {
        var timers = new List<TaskCompletionSource>();
        vm.Delay = (duration, token) =>
        {
            Assert.Equal(MainViewModel.MessageDuration, duration);
            var timer = new TaskCompletionSource();
            token.Register(() => timer.TrySetCanceled(token));
            timers.Add(timer);
            return timer.Task;
        };
        return timers;
    }

    [Fact]
    public void AMessageDisappearsAfterItsTime()
    {
        var vm = new MainViewModel(new AppSettings(), new FakeDialogs());
        var timers = ControlTime(vm);

        vm.ReportPageWithoutText(0);
        Assert.StartsWith("Page 1 contains no text.", vm.StatusMessage);
        timers[0].SetResult();
        Assert.Equal("", vm.StatusMessage);
    }

    [Fact]
    public void ShowingTheSameMessageAgainRestartsItsTime()
    {
        var vm = new MainViewModel(new AppSettings(), new FakeDialogs());
        var timers = ControlTime(vm);

        vm.ReportPageWithoutText(0);
        vm.ReportPageWithoutText(0);   // e.g. a second click shortly before the first message would vanish
        Assert.True(timers[0].Task.IsCanceled);
        timers[0].TrySetResult();      // the first timer is gone and must not clear the new message
        Assert.StartsWith("Page 1 contains no text.", vm.StatusMessage);
        timers[1].SetResult();
        Assert.Equal("", vm.StatusMessage);
    }

    [Fact]
    public void AnOldTimerLeavesANewerMessageAlone()
    {
        var vm = new MainViewModel(new AppSettings(), new FakeDialogs());
        var timers = ControlTime(vm);

        vm.ReportPageWithoutText(0);
        vm.ReportPageWithoutText(1);
        timers[0].TrySetResult();
        Assert.StartsWith("Page 2 contains no text.", vm.StatusMessage);
        timers[1].SetResult();
        Assert.Equal("", vm.StatusMessage);
    }
}
