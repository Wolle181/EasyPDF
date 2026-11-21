// Small helpers the browser program calls (JSImport, module "interop")
export function removeSplash() {
    document.getElementById('splash')?.remove();
}

export function setTitle(title) {
    document.title = title;
}

export function openTab(url) {
    // Without 'noopener' (with it the result is always null and a blocked popup cannot be told); the link back is cut instead
    const tab = window.open(url, '_blank');
    if (!tab)
        return false;
    tab.opener = null;
    return true;
}

// The introduction video above the page (the program draws into a canvas: the video element lies on top of it)
export function playVideo(url, closeLabel) {
    if (document.getElementById('video-overlay'))
        return;
    const overlay = document.createElement('div');
    overlay.id = 'video-overlay';
    const video = document.createElement('video');
    video.src = url;
    video.controls = true;
    video.autoplay = true;
    const close = document.createElement('button');
    close.textContent = closeLabel;
    const end = () => { video.pause(); overlay.remove(); document.removeEventListener('keydown', onKey, true); };
    const onKey = e => { if (e.key === 'Escape') { e.stopPropagation(); e.preventDefault(); end(); } };
    close.addEventListener('click', end);
    overlay.addEventListener('click', e => { if (e.target === overlay) end(); });
    document.addEventListener('keydown', onKey, true);
    overlay.append(video, close);
    document.body.append(overlay);
}

// The settings stay in the browser (localStorage, per site); private windows or blocked storage just start fresh
export function loadSettings() {
    try { return localStorage.getItem('settings') ?? ''; } catch { return ''; }
}

export function saveSettings(json) {
    try { localStorage.setItem('settings', json); } catch { }
}
