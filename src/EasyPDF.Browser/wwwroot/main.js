import { dotnet } from './_framework/dotnet.js'

const runtime = await dotnet
    .withDiagnosticTracing(false)
    .create();

const config = runtime.getConfig();
// The page's address is the base for the API calls
await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
