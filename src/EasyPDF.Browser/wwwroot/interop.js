// Small helpers the browser program calls (JSImport, module "interop")
export function removeSplash() {
    document.getElementById('splash')?.remove();
}

export function setTitle(title) {
    document.title = title;
}

// The settings stay in the browser (localStorage, per site); private windows or blocked storage just start fresh
export function loadSettings() {
    try { return localStorage.getItem('settings') ?? ''; } catch { return ''; }
}

export function saveSettings(json) {
    try { localStorage.setItem('settings', json); } catch { }
}
