using Avalonia;
using Avalonia.Headless;
using EasyPDF.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace EasyPDF.App.Tests;

public class TestAppBuilder
{
    // Texts are asserted in English regardless of the OS language
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void UseEnglish() => EasyPDF.App.Localization.Loc.SetCulture(new System.Globalization.CultureInfo("en"));

    // Real Skia rendering (not just the null renderer) so drawing code paths are exercised
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
