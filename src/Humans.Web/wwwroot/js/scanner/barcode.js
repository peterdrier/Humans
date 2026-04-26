// Scanner section — phase 1 barcode decode.
//
// Feature-detects the native BarcodeDetector API (Chrome/Edge/most Android). Falls back
// to @zxing/browser via CDN for iOS Safari and anywhere else the native API isn't shipped.
// Everything runs in the browser — no server round trip, no state written.

const ZXING_CDN_URL = 'https://cdn.jsdelivr.net/npm/@zxing/browser@0.1.5/+esm';
const DEDUPE_WINDOW_MS = 1500;

export function initBarcodeScanner(refs) {
    const {
        startButton,
        stopButton,
        video,
        results,
        resultsEmpty,
        status,
        error,
        labels,
    } = refs;

    let mediaStream = null;
    let nativeDetector = null;
    let nativeLoopHandle = null;
    let zxingReader = null;
    let zxingControls = null;
    let decodePath = null; // 'native' | 'zxing'
    const recentHits = new Map(); // dedupe key → timestamp

    function setStatus(message) {
        status.textContent = message ?? '';
    }

    function showError(message) {
        if (!message) {
            error.classList.add('d-none');
            error.textContent = '';
            return;
        }
        error.textContent = message;
        error.classList.remove('d-none');
    }

    function addResult(value, format) {
        if (!value) return;

        const dedupeKey = `${format}|${value}`;
        const now = Date.now();
        const previous = recentHits.get(dedupeKey);
        if (previous && now - previous < DEDUPE_WINDOW_MS) {
            return;
        }
        recentHits.set(dedupeKey, now);

        if (resultsEmpty && !resultsEmpty.classList.contains('d-none')) {
            resultsEmpty.classList.add('d-none');
        }

        const item = document.createElement('li');
        item.className = 'list-group-item';

        const formatBadge = document.createElement('span');
        formatBadge.className = 'badge bg-secondary me-2';
        formatBadge.textContent = format || 'UNKNOWN';

        const timestamp = document.createElement('small');
        timestamp.className = 'text-muted float-end';
        timestamp.textContent = new Date().toLocaleTimeString();

        const valueNode = renderValueNode(value);

        item.appendChild(formatBadge);
        item.appendChild(valueNode);
        item.appendChild(timestamp);
        results.insertBefore(item, results.firstChild);
    }

    function renderValueNode(value) {
        let parsed;
        try {
            parsed = new URL(value);
        } catch {
            parsed = null;
        }
        if (parsed && (parsed.protocol === 'http:' || parsed.protocol === 'https:')) {
            const anchor = document.createElement('a');
            anchor.href = value;
            anchor.textContent = value;
            anchor.target = '_blank';
            anchor.rel = 'noopener noreferrer';
            return anchor;
        }
        const span = document.createElement('code');
        span.className = 'text-break';
        span.textContent = value;
        return span;
    }

    async function start() {
        showError(null);
        setStatus(labels.starting);
        startButton.disabled = true;

        try {
            mediaStream = await navigator.mediaDevices.getUserMedia({
                video: { facingMode: 'environment' },
                audio: false,
            });
        } catch (err) {
            console.error('Scanner: camera access failed', err);
            showError(labels.errorCamera);
            setStatus(labels.stopped);
            startButton.disabled = false;
            return;
        }

        video.srcObject = mediaStream;
        try {
            await video.play();
        } catch (err) {
            console.warn('Scanner: video.play() rejected', err);
        }

        stopButton.disabled = false;

        if ('BarcodeDetector' in window) {
            decodePath = 'native';
            console.info('Scanner: using native BarcodeDetector');
            setStatus(`${labels.running} (${labels.pathNative})`);
            startNativeLoop();
            return;
        }

        decodePath = 'zxing';
        console.info('Scanner: falling back to @zxing/browser via CDN');
        setStatus(`${labels.running} (${labels.pathZxing})`);
        try {
            await startZxing();
        } catch (err) {
            console.error('Scanner: zxing bootstrap failed', err);
            showError(labels.errorNoDecoder);
            await stop();
        }
    }

    function startNativeLoop() {
        try {
            nativeDetector = new window.BarcodeDetector({
                formats: ['qr_code', 'code_128', 'code_39', 'ean_13', 'ean_8', 'upc_a', 'upc_e', 'pdf417', 'data_matrix'],
            });
        } catch (err) {
            // Some browsers report BarcodeDetector but constructors fail for unsupported formats.
            console.warn('Scanner: BarcodeDetector constructor failed, falling back to ZXing', err);
            decodePath = 'zxing';
            setStatus(`${labels.running} (${labels.pathZxing})`);
            startZxing().catch((e) => {
                console.error('Scanner: zxing fallback also failed', e);
                showError(labels.errorNoDecoder);
                stop();
            });
            return;
        }

        const scan = async () => {
            if (!mediaStream) return;
            try {
                const codes = await nativeDetector.detect(video);
                for (const code of codes) {
                    addResult(code.rawValue, code.format?.toUpperCase?.() ?? 'UNKNOWN');
                }
            } catch (err) {
                // Transient detect() failures are expected while the video warms up.
                console.debug('Scanner: detect() transient error', err);
            }
            if (mediaStream) {
                nativeLoopHandle = requestAnimationFrame(scan);
            }
        };

        nativeLoopHandle = requestAnimationFrame(scan);
    }

    async function startZxing() {
        const mod = await import(/* @vite-ignore */ ZXING_CDN_URL);
        const BrowserMultiFormatReader = mod.BrowserMultiFormatReader ?? mod.default?.BrowserMultiFormatReader;
        if (!BrowserMultiFormatReader) {
            throw new Error('BrowserMultiFormatReader not found in ZXing module export');
        }

        zxingReader = new BrowserMultiFormatReader();
        zxingControls = await zxingReader.decodeFromStream(mediaStream, video, (result, err) => {
            if (result) {
                const format = result.getBarcodeFormat?.()?.toString?.() ?? 'UNKNOWN';
                addResult(result.getText(), format);
            }
            // err is a NotFoundException on every empty frame — ignore silently.
        });
    }

    async function stop() {
        if (nativeLoopHandle) {
            cancelAnimationFrame(nativeLoopHandle);
            nativeLoopHandle = null;
        }
        if (zxingControls) {
            try {
                zxingControls.stop();
            } catch (err) {
                console.debug('Scanner: zxing controls.stop error', err);
            }
            zxingControls = null;
        }
        if (zxingReader) {
            try {
                zxingReader.reset?.();
            } catch (err) {
                console.debug('Scanner: zxing reader.reset error', err);
            }
            zxingReader = null;
        }
        if (mediaStream) {
            for (const track of mediaStream.getTracks()) {
                try { track.stop(); } catch (err) { console.debug('Scanner: track.stop error', err); }
            }
            mediaStream = null;
        }
        video.srcObject = null;
        setStatus(labels.stopped);
        startButton.disabled = false;
        stopButton.disabled = true;
        decodePath = null;
    }

    startButton.addEventListener('click', start);
    stopButton.addEventListener('click', stop);
    window.addEventListener('pagehide', stop);
    window.addEventListener('beforeunload', stop);
}
