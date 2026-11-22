# Open tasks

Tasks that are planned but deliberately not done yet.

## November 2027 (.NET 12): web version on CoreCLR, AOT without exceptions

Check again when .NET 12 is released (expected November 2027).

- **WebAssembly runtime:** the web version runs on Mono (the only runtime with AOT in the browser in .NET 11). .NET 12 is
  expected to make CoreCLR the stable WebAssembly runtime (in .NET 11 only an opt-in preview without AOT/native
  toolchain). Check whether the browser program can move to CoreCLR, what replaces `RunAOTCompilation`, and measure
  again with `tools/web-timing` (start, dialogs, download size) against the current numbers in DESIGN.md "Start on the
  web".
- **SkiaSharp/HarfBuzzSharp interpreted:** Mono's AOT compiler aborts on SkiaSharp 4.157 / HarfBuzzSharp 14.4
  (assertion in `sgen-alloc.c:409`), so `EasyPDF.Browser.csproj` keeps them interpreted
  (`_AOT_InternalForceInterpretAssemblies`, internal to the WebAssembly SDK). Tested on 2026-11-22 with a minimal project:
  .NET 11 and .NET 10 crash with these versions, .NET 11 with SkiaSharp 3.119 / HarfBuzzSharp 8.3 compiles; so it comes
  from the new packages, not from .NET 11. With newer packages or the new runtime, try removing the exception (also
  check whether the internal item still exists).
