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

// A file the server sends as attachment: a link with "download" saves it without leaving the page
export function download(url) {
    const link = document.createElement('a');
    link.href = url;
    link.download = '';
    document.body.append(link);
    link.click();
    link.remove();
}

export function platform() {
    return navigator.userAgentData?.platform || navigator.userAgent || '';
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

// Stamps and templates: one ZIP file (Base64) in IndexedDB, which holds much more than localStorage
function openStore() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open('app', 1);
        request.onupgradeneeded = () => request.result.createObjectStore('files');
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

export function loadLibrary() {
    return loadStored('library');
}

export function saveLibrary(zipBase64) {
    return saveStored('library', zipBase64);
}

// A text under a key of the store (also the server's library with its ETag); empty if there is none or storage is blocked
export async function loadStored(key) {
    try {
        const db = await openStore();
        return await new Promise(resolve => {
            const get = db.transaction('files').objectStore('files').get(key);
            get.onsuccess = () => resolve(typeof get.result === 'string' ? get.result : '');
            get.onerror = () => resolve('');
        });
    } catch { return ''; }
}

export async function saveStored(key, value) {
    try {
        const db = await openStore();
        await new Promise(resolve => {
            const transaction = db.transaction('files', 'readwrite');
            transaction.objectStore('files').put(value, key);
            transaction.oncomplete = resolve;
            transaction.onerror = resolve;
            transaction.onabort = resolve;
        });
    } catch { }
}

// Send by email: the system's share sheet (Windows, macOS, Android, iOS) offers the email program, which opens a new
// message with the PDF attached. Browsers allow it only shortly after a click: if preparing took longer, the program asks
// for another click and calls again, the fetched file is kept for that.
export function canShareFiles() {
    try {
        return !!navigator.canShare
            && navigator.canShare({ files: [new File([new Uint8Array(1)], 'a.pdf', { type: 'application/pdf' })] });
    } catch { return false; }
}

let pendingShare = null;

export async function shareFile(url, name, title) {
    if (pendingShare?.url !== url) {
        const response = await fetch(url, { credentials: 'same-origin' });
        if (!response.ok)
            throw new Error('HTTP ' + response.status);
        pendingShare = { url, file: new File([await response.blob()], name, { type: 'application/pdf' }) };
    }
    try {
        await navigator.share({ files: [pendingShare.file], title });
        pendingShare = null;
        return 'shared';
    } catch (e) {
        if (e?.name === 'NotAllowedError')
            return 'activation';
        pendingShare = null;
        if (e?.name === 'AbortError')
            return 'cancelled';
        throw e;
    }
}

// The other tabs of this site in this browser (Concatenate: "Open documents"): the program exchanges small JSON messages
// with them. A BroadcastChannel reaches all tabs of the same origin except the sending one.
let tabChannel = null;

export function openTabChannel(receive) {
    if (typeof BroadcastChannel === 'undefined')
        return false;
    tabChannel = new BroadcastChannel('documents');
    tabChannel.onmessage = e => { if (typeof e.data === 'string') receive(e.data); };
    return true;
}

export function postTabMessage(message) {
    tabChannel?.postMessage(message);
}
