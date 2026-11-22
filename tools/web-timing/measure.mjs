// Measures the web version in a real browser: Firefox (headless) driven over WebDriver BiDi, no extra packages (Node 22+
// has WebSocket built in; Node 20 needs --experimental-websocket). Opens <url>?benchmark twice in the same profile: first with an empty cache, then with the
// cache of the first visit. Prints the program's "[timing]" lines (start phases, dialogs; see WebTiming in
// src/EasyPDF.UI/Services) and how many bytes came over the network.
//
//   node tools/web-timing/measure.mjs http://localhost:5199/ [--runs 1] [--firefox /usr/bin/firefox]
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const args = process.argv.slice(2);
const option = (name, fallback) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : fallback; };
const base = args.find(a => /^https?:/.test(a)) ?? 'http://localhost:5199/';
const firefox = option('--firefox', 'firefox');
const runs = Number(option('--runs', '1'));
const port = 9300 + Math.floor(Math.random() * 500);

function startFirefox(profile) {
    return new Promise((resolve, reject) => {
        const child = spawn(firefox, ['--headless', '--no-remote', '--profile', profile, '--window-size=1400,900',
            `--remote-debugging-port=${port}`, 'about:blank'], { stdio: ['ignore', 'pipe', 'pipe'] });
        const timer = setTimeout(() => reject(new Error('Firefox did not start the remote agent')), 30000);
        const watch = data => {
            if (/WebDriver BiDi listening on (ws:\/\/\S+)/.test(data.toString())) {
                clearTimeout(timer);
                resolve(child);
            }
        };
        child.stderr.on('data', watch);
        child.stdout.on('data', watch);
        child.on('error', reject);
    });
}

class Bidi {
    constructor(socket) {
        this.socket = socket;
        this.next = 1;
        this.pending = new Map();
        this.listeners = [];
        socket.addEventListener('message', e => {
            const message = JSON.parse(e.data);
            if (message.id && this.pending.has(message.id)) {
                const { resolve, reject } = this.pending.get(message.id);
                this.pending.delete(message.id);
                message.type === 'error' ? reject(new Error(`${message.error}: ${message.message}`)) : resolve(message.result);
            } else if (message.type === 'event')
                this.listeners.forEach(l => l(message));
        });
    }

    static open(url) {
        return new Promise((resolve, reject) => {
            const socket = new WebSocket(url);
            socket.addEventListener('open', () => resolve(new Bidi(socket)));
            socket.addEventListener('error', () => reject(new Error('cannot connect to ' + url)));
        });
    }

    send(method, params = {}) {
        const id = this.next++;
        this.socket.send(JSON.stringify({ id, method, params }));
        return new Promise((resolve, reject) => this.pending.set(id, { resolve, reject }));
    }
}

async function visit(bidi, context, label) {
    const lines = [];
    const done = new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(`${label}: no "benchmark done" within 180 s`)), 180000);
        bidi.listeners = [message => {
            if (message.method !== 'log.entryAdded')
                return;
            const text = message.params.text ?? '';
            if (message.params.level === 'error')
                lines.push('  (console error) ' + text);
            if (!text.includes('[timing]'))
                return;
            lines.push('  ' + text.replace('[timing] ', ''));
            if (text.includes('benchmark done')) {
                clearTimeout(timer);
                resolve();
            }
        }];
    });
    const url = new URL(base);
    url.searchParams.set('benchmark', '');
    await bidi.send('browsingContext.navigate', { context, url: url.toString().replace('benchmark=', 'benchmark'), wait: 'none' });
    await done;
    const network = await bidi.send('script.evaluate', {
        target: { context }, awaitPromise: false,
        expression: `(() => { const r = performance.getEntriesByType('resource'), n = performance.getEntriesByType('navigation')[0];
            const bytes = r.reduce((a, e) => a + e.transferSize, n ? n.transferSize : 0);
            const fromCache = r.filter(e => e.transferSize === 0 && e.decodedBodySize > 0).length;
            const largest = r.filter(e => e.transferSize > 50000).sort((a, b) => b.transferSize - a.transferSize).slice(0, 5)
                .map(e => new URL(e.name).pathname + ' ' + (e.transferSize / 1e6).toFixed(2) + ' MB');
            return JSON.stringify({ bytes, requests: r.length + 1, fromCache, largest }); })()`,
    });
    const net = JSON.parse(network.result.value);
    console.log(`== ${label}: ${(net.bytes / 1e6).toFixed(2)} MB over the network, ${net.requests} requests, ${net.fromCache} from the cache without asking`);
    if (net.largest.length > 0)
        console.log('  largest transfers: ' + net.largest.join(', '));
    lines.forEach(l => console.log(l));
}

for (let run = 1; run <= runs; run++) {
    const profile = mkdtempSync(join(tmpdir(), 'web-timing-'));
    const browser = await startFirefox(profile);
    try {
        const bidi = await Bidi.open(`ws://127.0.0.1:${port}/session`);
        await bidi.send('session.new', { capabilities: {} });
        await bidi.send('session.subscribe', { events: ['log.entryAdded'] });
        const tree = await bidi.send('browsingContext.getTree', {});
        const context = tree.contexts[0].context;
        await visit(bidi, context, `run ${run}, empty cache`);
        await visit(bidi, context, `run ${run}, second visit`);
        await bidi.send('session.end').catch(() => { });
    } finally {
        browser.kill();
        await new Promise(r => setTimeout(r, 1000));
        rmSync(profile, { recursive: true, force: true });
    }
}
process.exit(0);
