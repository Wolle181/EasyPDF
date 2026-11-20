// Small helpers the browser program calls (JSImport, module "interop")
export function removeSplash() {
    document.getElementById('splash')?.remove();
}

export function setTitle(title) {
    document.title = title;
}
