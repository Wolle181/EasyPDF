import { dotnet } from './_framework/dotnet.js'

const runtime = await dotnet
    .withDiagnosticTracing(false)
    // The runtime loads only the satellite assemblies of the start culture; switching the language in the settings
    // needs the others too (otherwise the texts stay in the fallback language)
    .withConfig({ loadAllSatelliteResources: true })
    .create();

const config = runtime.getConfig();
// The page's address is the base for the API calls
await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
