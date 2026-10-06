const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const vm = require('node:vm');

function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}
const settle = () => new Promise(resolve => setImmediate(resolve));
function stream() {
    const track = { stops: 0, stop() { this.stops++; } };
    return { track, getTracks: () => [track] };
}

function scanner({ native = true, slowPlay = false } = {}) {
    const button = () => ({ disabled: false, handlers: {}, addEventListener(event, handler) { this.handlers[event] = handler; } });
    const startButton = button(), stopButton = button();
    const cameraRequests = [], detects = [], frames = new Map(), hits = [], decodes = [];
    const module = deferred(), play = deferred();
    const video = { srcObject: null, play: () => slowPlay ? play.promise : Promise.resolve() };
    const status = { textContent: '' };
    const error = { textContent: '', classList: { add() {}, remove() {} } };
    const windowHandlers = {};
    const window = { addEventListener: (event, handler) => { windowHandlers[event] = handler; } };
    if (native) window.BarcodeDetector = class {
        detect() { const call = deferred(); detects.push(call); return call.promise; }
    };
    const zxing = { BrowserMultiFormatReader: class {
        decodeFromStream(media, preview, callback) {
            const call = deferred();
            const controls = { stops: 0, stop() { this.stops++; media.track.stop(); preview.srcObject = null; } };
            decodes.push({ ...call, controls, callback, media });
            return call.promise;
        }
    } };
    let nextFrame = 0;
    // Replace only the CDN module load with a controlled promise; execute the production lifecycle.
    const source = readFileSync(resolve(__dirname,
        '../../../src/Sections/Humans.Scanner/wwwroot/js/scanner/barcode.js'), 'utf8')
        .replace('export function initBarcodeScanner', 'function initBarcodeScanner')
        .replace('await import(ZXING_CDN_URL)', 'await loadZxingModule()');
    const context = {
        window, console: { info() {}, warn() {}, debug() {}, error() {} },
        navigator: { mediaDevices: { getUserMedia() { const call = deferred(); cameraRequests.push(call); return call.promise; } } },
        requestAnimationFrame: callback => { frames.set(++nextFrame, callback); return nextFrame; },
        cancelAnimationFrame: id => frames.delete(id),
        loadZxingModule: () => module.promise,
    };
    vm.createContext(context);
    vm.runInContext(source, context);
    context.initBarcodeScanner({ startButton, stopButton, video, status, error,
        results: null, resultsEmpty: null, onHit: value => hits.push(value),
        labels: { starting: 'Starting', stopped: 'Stopped', running: 'Running', pathNative: 'Native', pathZxing: 'ZXing', errorCamera: 'Camera failed', errorNoDecoder: 'Decoder failed' } });
    return {
        cameraRequests, detects, frames, hits, decodes, video, status, error, startButton, stopButton, play,
        start: () => startButton.handlers.click(), stop: () => stopButton.handlers.click(),
        pagehide: () => windowHandlers.pagehide(),
        loadModule: () => module.resolve(zxing),
        frame: () => { const [id, callback] = frames.entries().next().value; frames.delete(id); return callback(); },
    };
}

test('page exit releases camera permission that resolves after startup was abandoned', async () => {
    const s = scanner(); const starting = s.start(); s.pagehide();
    const camera = stream(); s.cameraRequests[0].resolve(camera); await starting;
    assert.equal(camera.track.stops, 1);
    assert.equal(s.video.srcObject, null);
    assert.equal(s.frames.size, 0);
    assert.equal(s.status.textContent, 'Stopped');
});

test('an older camera acquisition cannot replace a restarted camera', async () => {
    const s = scanner(); const oldStart = s.start(); s.stop(); const newStart = s.start();
    await settle();
    const current = stream(); s.cameraRequests[1].resolve(current); await newStart;
    const old = stream(); s.cameraRequests[0].resolve(old); await oldStart;
    assert.equal(old.track.stops, 1);
    assert.equal(current.track.stops, 0);
    assert.equal(s.video.srcObject, current);
    assert.equal(s.frames.size, 1);
});

test('stop during video playback startup cannot start a decoder afterward', async () => {
    const s = scanner({ slowPlay: true }); const starting = s.start();
    s.cameraRequests[0].resolve(stream()); await settle(); s.stop(); s.play.resolve(); await starting;
    assert.equal(s.frames.size, 0);
    assert.equal(s.stopButton.disabled, true);
    assert.equal(s.status.textContent, 'Stopped');
});

for (const restart of [false, true]) {
    test(`a pending native detection cannot deliver a stopped session's result (restart: ${restart})`, async () => {
        const s = scanner(); const starting = s.start(); s.cameraRequests[0].resolve(stream()); await starting;
        const detecting = s.frame(); s.stop();
        if (restart) {
            const currentStart = s.start(); await settle(); s.cameraRequests[1].resolve(stream()); await currentStart;
        }
        s.detects[0].resolve([{ rawValue: 'OLD', format: 'qr_code' }]); await detecting;
        assert.equal(s.hits.length, 0);
        assert.equal(s.frames.size, restart ? 1 : 0);
    });
}

test('a module loaded after stop cannot start ZXing with a released stream', async () => {
    const s = scanner({ native: false }); const starting = s.start();
    s.cameraRequests[0].resolve(stream()); await settle(); s.stop(); s.loadModule(); await settle();
    if (s.decodes[0]) s.decodes[0].resolve(s.decodes[0].controls);
    await starting;
    assert.equal(s.decodes.length, 0);
    assert.equal(s.status.textContent, 'Stopped');
});

test('late ZXing controls are stopped and their callbacks cannot publish results', async () => {
    const s = scanner({ native: false }); const starting = s.start();
    s.cameraRequests[0].resolve(stream()); s.loadModule(); await settle(); s.stop();
    s.decodes[0].callback({ getText: () => 'OLD' });
    s.decodes[0].resolve(s.decodes[0].controls); await starting;
    assert.equal(s.decodes[0].controls.stops, 1);
    assert.equal(s.hits.length, 0);
    assert.equal(s.video.srcObject, null);
});

test('restart waits for stale ZXing controls before opening the next camera', async () => {
    const s = scanner({ native: false }); const oldStart = s.start();
    s.cameraRequests[0].resolve(stream()); s.loadModule(); await settle(); s.stop(); const newStart = s.start();
    await settle(); assert.equal(s.cameraRequests.length, 1);
    s.decodes[0].resolve(s.decodes[0].controls); await oldStart; await settle();
    const current = stream(); s.cameraRequests[1].resolve(current); await settle();
    s.decodes[1].resolve(s.decodes[1].controls); await newStart;
    assert.equal(s.decodes[0].controls.stops, 1);
    assert.equal(s.video.srcObject, current);
    assert.equal(current.track.stops, 0);
});

test('the active native session still publishes scans and schedules its next frame', async () => {
    const s = scanner(); const starting = s.start(); const camera = stream();
    s.cameraRequests[0].resolve(camera); await starting; const detecting = s.frame();
    s.detects[0].resolve([{ rawValue: 'CURRENT', format: 'qr_code' }]); await detecting;
    assert.equal(s.hits[0], 'CURRENT'); assert.equal(s.frames.size, 1);
    s.stop(); assert.equal(camera.track.stops, 1); assert.equal(s.frames.size, 0);
});

test('the active ZXing session still publishes scans and releases its controls on stop', async () => {
    const s = scanner({ native: false }); const starting = s.start(); const camera = stream();
    s.cameraRequests[0].resolve(camera); s.loadModule(); await settle();
    s.decodes[0].resolve(s.decodes[0].controls); await starting;
    s.decodes[0].callback({ getText: () => 'CURRENT' });
    assert.equal(s.hits[0], 'CURRENT');
    s.stop(); assert.equal(s.decodes[0].controls.stops, 1); assert.equal(s.video.srcObject, null);
});

test('an obsolete camera failure does not reset the restarted scanner', async () => {
    const s = scanner(); const oldStart = s.start(); s.stop(); const newStart = s.start();
    await settle(); const current = stream(); s.cameraRequests[1].resolve(current); await newStart;
    s.cameraRequests[0].reject(new Error('old permission denied')); await oldStart;
    assert.equal(s.error.textContent, ''); assert.equal(s.video.srcObject, current);
    assert.equal(s.startButton.disabled, true); assert.equal(s.stopButton.disabled, false);
});

test('a current camera failure shows its error and restores stopped controls', async () => {
    const s = scanner(); const starting = s.start();
    s.cameraRequests[0].reject(new Error('permission denied')); await starting;
    assert.equal(s.error.textContent, 'Camera failed'); assert.equal(s.status.textContent, 'Stopped');
    assert.equal(s.startButton.disabled, false); assert.equal(s.stopButton.disabled, true);
});
