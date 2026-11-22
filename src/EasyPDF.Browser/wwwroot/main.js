import { dotnet } from './_framework/dotnet.js'

// The splash shows what happens: the first visit downloads several megabytes, then .NET and the program start. The
// language is the one chosen in the settings (kept in the browser), otherwise the browser's.
const german = (() => {
    try {
        const language = JSON.parse(localStorage.getItem('settings') || '{}').Language;
        if (language === 'de' || language === 'en')
            return language === 'de';
    } catch { }
    return (navigator.language || '').toLowerCase().startsWith('de');
})();
const splashText = document.getElementById('splash-text');
const progressBar = document.getElementById('splash-progress');
const status = (text, fraction) => {
    if (splashText)
        splashText.textContent = text;
    if (progressBar)
        progressBar.style.width = `${Math.round(fraction * 100)}%`;
};
const loading = german ? 'Wird geladen …' : 'Loading …';
status(loading, 0);

let builder = dotnet
    .withDiagnosticTracing(false)
    // The runtime loads only the satellite assemblies of the start culture; switching the language in the settings
    // needs the others too (otherwise the texts stay in the fallback language)
    .withConfig({ loadAllSatelliteResources: true });
if (typeof builder.withModuleConfig === 'function')
    builder = builder.withModuleConfig({
        onDownloadResourceProgress: (loaded, total) => {
            if (total > 0)
                status(`${loading} ${Math.round(loaded * 100 / total)} %`, loaded / total);
        },
    });
const runtime = await builder.create();
status(german ? 'Wird gestartet …' : 'Starting …', 1);

const config = runtime.getConfig();
// The page's address is the base for the API calls
await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
