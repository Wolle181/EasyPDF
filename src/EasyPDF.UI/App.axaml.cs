using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EasyPDF.App.Services;
using EasyPDF.App.Views;
using EasyPDF.Core.Settings;

namespace EasyPDF.App;

public class App : Application
{
    /// <summary>Files and mode from the command line (set by <see cref="Program.Main"/>).</summary>
    public static StartupArguments Startup { get; set; } = StartupArguments.Parse([]);

    /// <summary>Receives the files of later starts (null if another process already had that role).</summary>
    public static InstanceChannel? Instance { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            var changed = InstallerChoice.ApplyTo(settings);
            AppEnvironment.Apply(settings);
            // Stamp and templates folders with their samples (sample templates in the language just applied)
            changed |= SampleContent.Provide(settings);
            if (changed)
                TrySave(settings);
            var windows = new WindowManager(settings);
            var window = windows.CreateWindow();
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            // Files can only be opened once the window is shown (dialogs need an owner)
            var shown = new TaskCompletionSource();
            window.Opened += (_, _) => shown.TrySetResult();
            async Task OpenFilesAsync(StartupArguments request)
            {
                await shown.Task;
                if (request.Read && !request.Merge)
                    await windows.OpenEachAsync(request.Files);
                else
                    await windows.OpenFilesAsync(request.Files, request.Merge);
            }

            // PDFs passed on the command line or by the context menu: one is opened, several (or --merge) go to the
            // Concatenate dialog, with --read each one gets a window
            var startupFiles = Startup.Files.Count > 0 ? OpenFilesAsync(Startup) : Task.CompletedTask;

            // Linux/macOS: once, after the start files are open, ask whether the program should open PDF files
            async Task AskDefaultAppAsync()
            {
                await shown.Task;
                await startupFiles;
                var asked = await DefaultAppPrompt.AskOnceAsync(settings, SystemDefaultPdfApp.Instance,
                    () => new Dialogs.QuestionDialog(Localization.Loc.T("DefaultAppQuestionTitle"), Localization.Loc.T("DefaultAppQuestion"),
                        Localization.Loc.T("DefaultAppQuestionDetail")).ShowDialog<bool>(window),
                    OperatingSystem.IsWindows());
                if (asked)
                    TrySave(settings);
            }
            _ = AskDefaultAppAsync();

            // Later starts hand over their files (or, without files, ask for a window)
            Instance?.SetHandler(request => Dispatcher.UIThread.Post(() => _ = OpenFilesAsync(request)));

            // Quit from the macOS menu/Dock or the system: ask about unsaved changes window by window
            desktop.ShutdownRequested += (_, e) =>
            {
                if (windows.Windows.Any(w => w.NeedsCloseConfirmation))
                {
                    e.Cancel = true;
                    _ = windows.CloseAllAsync();
                }
            };

            // macOS passes files from Finder ("Open With", dropping on the Dock icon) as activation, not as arguments
            if (OperatingSystem.IsMacOS() && this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs { Files: { Count: > 0 } items })
                        _ = OpenFilesAsync(new StartupArguments([.. items.Select(i => i.TryGetLocalPath()).OfType<string>()], false));
                };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            // Web version: one view, the page itself; settings live in the browser's memory for this visit
            var settings = AppSettings.Load();
            AppEnvironment.Apply(settings);
            single.MainView = new MainView(settings);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void TrySave(AppSettings settings)
    {
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Not fatal: the choice is applied for this run and taken over again next time
        }
    }
}
