using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using EasyPDF.App.Dialogs;
using EasyPDF.App.Localization;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;
using EasyPDF.Core;
using EasyPDF.Core.Settings;
using Xunit;

namespace EasyPDF.App.Tests;

/// <summary>"Open PDF files with …": the button in the settings and the question at the first start (Linux, macOS).</summary>
public class DefaultPdfAppTests
{
    private sealed class FakeDefaultApp(bool? isDefault, DefaultAppResult result = DefaultAppResult.Done) : IDefaultPdfApp
    {
        public bool? Default = isDefault;
        public int MadeDefault;

        public bool? IsDefault() => Default;

        public Task<DefaultAppResult> MakeDefaultAsync()
        {
            MadeDefault++;
            if (result == DefaultAppResult.Done)
                Default = true;
            return Task.FromResult(result);
        }
    }

    private static async Task Pump(int ms = 200)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static Point CenterOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    [Fact]
    public async Task TheFirstStartAsksOnceAndOnlyWhereItMakesSense()
    {
        var settings = new AppSettings();
        var app = new FakeDefaultApp(false);
        var asked = 0;
        Task<bool> Yes() { asked++; return Task.FromResult(true); }

        Assert.False(await DefaultAppPrompt.AskOnceAsync(settings, app, Yes, isWindows: true));   // the MSI asks there
        Assert.False(await DefaultAppPrompt.AskOnceAsync(settings, new FakeDefaultApp(true), Yes, isWindows: false));
        Assert.False(await DefaultAppPrompt.AskOnceAsync(settings, new FakeDefaultApp(null), Yes, isWindows: false)); // not installed
        Assert.Equal(0, asked);
        Assert.False(settings.DefaultAppAsked);

        Assert.True(await DefaultAppPrompt.AskOnceAsync(settings, app, Yes, isWindows: false));
        Assert.Equal((1, 1), (asked, app.MadeDefault));
        Assert.True(settings.DefaultAppAsked);
        app.Default = false;                                     // even if it is no longer the default: never again
        Assert.False(await DefaultAppPrompt.AskOnceAsync(settings, app, Yes, isWindows: false));

        var no = new AppSettings();
        var other = new FakeDefaultApp(false);
        Assert.True(await DefaultAppPrompt.AskOnceAsync(no, other, () => Task.FromResult(false), isWindows: false));
        Assert.Equal(0, other.MadeDefault);
        Assert.True(no.DefaultAppAsked);
    }

    [Fact]
    public async Task TheSettingsShowTheStateAndTheButtonsResult()
    {
        var app = new FakeDefaultApp(false);
        var vm = new SettingsViewModel(new AppSettings(), () => { }, app);
        vm.RefreshDefaultAppStatus();
        Assert.Equal(Loc.T("DefaultAppIsOther"), vm.DefaultAppStatus);
        await vm.MakeDefaultCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("DefaultAppIsSet"), vm.DefaultAppStatus);

        var windows = new SettingsViewModel(new AppSettings(), () => { }, new FakeDefaultApp(false, DefaultAppResult.SettingsOpened));
        await windows.MakeDefaultCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("DefaultAppSettingsOpened"), windows.DefaultAppStatus);

        var archive = new SettingsViewModel(new AppSettings(), () => { }, new FakeDefaultApp(null, DefaultAppResult.NotAvailable));
        archive.RefreshDefaultAppStatus();
        Assert.Empty(archive.DefaultAppStatus);
        await archive.MakeDefaultCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("DefaultAppNotAvailable"), archive.DefaultAppStatus);
    }

    [Fact]
    public void TheLinuxPackageNameIsTheShownNameInLowerCase() =>
        Assert.Equal(new string([.. AppInfo.Name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]), SystemDefaultPdfApp.LinuxPackageName);

    [AvaloniaFact]
    public async Task TheSettingsButtonMakesTheProgramTheDefault()
    {
        var owner = new Window { Width = 600, Height = 700 };
        owner.Show();
        var app = new FakeDefaultApp(false);
        var settings = new SettingsWindow(new AppSettings(), () => { }, app);
        _ = settings.ShowDialog(owner);
        await Pump();
        var status = settings.FindControl<TextBlock>("DefaultAppStatusText")!;
        Assert.Equal(Loc.T("DefaultAppIsOther"), status.Text);
        var button = settings.FindControl<Button>("DefaultAppButton")!;
        Assert.Equal(Loc.T("SettingsMakeDefault"), button.Content);
        settings.MouseDown(CenterOf(button, settings), MouseButton.Left);
        settings.MouseUp(CenterOf(button, settings), MouseButton.Left);
        await Pump();
        Assert.Equal(1, app.MadeDefault);
        Assert.Equal(Loc.T("DefaultAppIsSet"), status.Text);
        settings.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TheQuestionAnswersYesOrNoAndClosingMeansNo()
    {
        var owner = new Window { Width = 600, Height = 500 };
        owner.Show();
        var question = new QuestionDialog(Loc.T("DefaultAppQuestionTitle"), Loc.T("DefaultAppQuestion"), Loc.T("DefaultAppQuestionDetail"));
        var answer = question.ShowDialog<bool>(owner);
        await Pump();
        var yes = question.FindControl<Button>("YesButton")!;
        question.MouseDown(CenterOf(yes, question), MouseButton.Left);
        question.MouseUp(CenterOf(yes, question), MouseButton.Left);
        Assert.True(await answer);

        var closed = new QuestionDialog(Loc.T("DefaultAppQuestionTitle"), Loc.T("DefaultAppQuestion"));
        var none = closed.ShowDialog<bool>(owner);
        await Pump();
        closed.Close();                                          // [X] = no
        Assert.False(await none);
        owner.Close();
    }
}
