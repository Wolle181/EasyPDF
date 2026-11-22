# Open tasks

Tasks that are planned but deliberately not done yet.

## Web version: measure dialog latency, try WebAssembly AOT

Every dialog in the web version takes a noticeable moment before it shows. Likely cause (not measured yet): the browser
program runs on .NET's IL interpreter (no `RunAOTCompilation` in `EasyPDF.Browser.csproj`/`Dockerfile`), and building a
dialog is code that runs rarely (XAML, Fluent control templates, styles, bindings, layout, text), which the interpreter
runs many times slower than the desktop's Native AOT; the first opening of each dialog type costs most. Drawing the
full-page overlay (dimmed page, 24 px blurred `BoxShadow`) may add to it with software rendering.

1. Measure: stopwatch in `MainView.ShowDialogAsync`/`DialogLayer.ShowAsync` (construction, first layout, first frame)
   written to the browser console; first and second opening of a few dialogs (Merge, Replace pages, Settings).
2. Build with `<RunAOTCompilation>true</RunAOTCompilation>` and compare: dialog times, download size (`.br`), start
   time, image build time.
3. Depending on the result: AOT, warming up the dialogs once after start while the browser is idle, or a lighter
   shadow.
