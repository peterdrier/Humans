// Camera barcode decode: the native BarcodeDetector API where shipped (Chrome/Edge/most
// Android), else @zxing/browser from CDN (iOS Safari). Nothing leaves the browser.

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
        onHit,
    } = refs;

    let currentSession = 0;
    let decoderStartup = null;
    let mediaStream = null;
    let nativeDetector = null;
    let nativeLoopHandle = null;
    let zxingReader = null;
    let zxingControls = null;
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

        if (onHit) onHit(value, format);

        if (resultsEmpty && !resultsEmpty.classList.contains('d-none')) {
            resultsEmpty.classList.add('d-none');
        }

        if (!results) return;

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
        const session = ++currentSession;
        showError(null);
        setStatus(labels.starting);
        startButton.disabled = true;
        stopButton.disabled = false;

        // ZXing controls clear the video source on stop: finish old cleanup before reusing it.
        // Its own start session handles failures; restart only waits for cleanup to finish.
        if (decoderStartup) await decoderStartup.catch(() => {});
        if (session !== currentSession) return;

        try {
            const stream = await navigator.mediaDevices.getUserMedia({
                video: { facingMode: 'environment' },
                audio: false,
            });
            if (session !== currentSession) {
                stopTracks(stream);
                return;
            }
            mediaStream = stream;
        } catch (err) {
            if (session !== currentSession) return;
            console.error('Scanner: camera access failed', err);
            showError(labels.errorCamera);
            setStatus(labels.stopped);
            startButton.disabled = false;
            stopButton.disabled = true;
            return;
        }

        video.srcObject = mediaStream;
        try {
            await video.play();
        } catch (err) {
            if (session !== currentSession) return;
            console.warn('Scanner: video.play() rejected', err);
            showError(labels.errorCamera);
            await stop();
            return;
        }

        if (session !== currentSession) return;

        if ('BarcodeDetector' in window) {
            console.info('Scanner: using native BarcodeDetector');
            setStatus(`${labels.running} (${labels.pathNative})`);
            startNativeLoop(session);
            return;
        }

        console.info('Scanner: falling back to @zxing/browser via CDN');
        setStatus(`${labels.running} (${labels.pathZxing})`);
        try {
            await startZxing(session);
        } catch (err) {
            if (session !== currentSession) return;
            console.error('Scanner: zxing bootstrap failed', err);
            showError(labels.errorNoDecoder);
            await stop();
        }
    }

    function startNativeLoop(session) {
        try {
            nativeDetector = new window.BarcodeDetector({
                formats: ['qr_code', 'code_128', 'code_39', 'ean_13', 'ean_8', 'upc_a', 'upc_e', 'pdf417', 'data_matrix'],
            });
        } catch (err) {
            // Some browsers report BarcodeDetector but constructors fail for unsupported formats.
            console.warn('Scanner: BarcodeDetector constructor failed, falling back to ZXing', err);
            setStatus(`${labels.running} (${labels.pathZxing})`);
            startZxing(session).catch((e) => {
                if (session !== currentSession) return;
                console.error('Scanner: zxing fallback also failed', e);
                showError(labels.errorNoDecoder);
                stop();
            });
            return;
        }

        const scan = async () => {
            if (session !== currentSession || !mediaStream) return;
            try {
                const codes = await nativeDetector.detect(video);
                if (session !== currentSession) return;
                for (const code of codes) {
                    addResult(code.rawValue, code.format?.toUpperCase?.() ?? 'UNKNOWN');
                }
            } catch (err) {
                // Transient detect() failures are expected while the video warms up.
                console.debug('Scanner: detect() transient error', err);
            }
            if (session === currentSession && mediaStream) {
                nativeLoopHandle = requestAnimationFrame(scan);
            }
        };

        nativeLoopHandle = requestAnimationFrame(scan);
    }

    async function startZxing(session) {
        const stream = mediaStream;
        const startup = (async () => {
            const mod = await import(ZXING_CDN_URL);
            if (session !== currentSession) return;
            const BrowserMultiFormatReader = mod.BrowserMultiFormatReader ?? mod.default?.BrowserMultiFormatReader;
            if (!BrowserMultiFormatReader) {
                throw new Error('BrowserMultiFormatReader not found in ZXing module export');
            }

            zxingReader = new BrowserMultiFormatReader();
            const controls = await zxingReader.decodeFromStream(stream, video, (result, err) => {
                if (session !== currentSession) return;
                if (result) {
                    const format = result.getBarcodeFormat?.()?.toString?.() ?? 'UNKNOWN';
                    addResult(result.getText(), format);
                }
                // err is a NotFoundException on every empty frame — ignore silently.
            });
            if (session !== currentSession) {
                await controls.stop();
                return;
            }
            zxingControls = controls;
        })();
        decoderStartup = startup;
        try {
            await startup;
        } finally {
            if (decoderStartup === startup) decoderStartup = null;
        }
    }

    function stopTracks(stream) {
        for (const track of stream.getTracks()) {
            try { track.stop(); } catch (err) { console.debug('Scanner: track.stop error', err); }
        }
    }

    function stop() {
        currentSession++;
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
            stopTracks(mediaStream);
            mediaStream = null;
        }
        video.srcObject = null;
        setStatus(labels.stopped);
        startButton.disabled = false;
        stopButton.disabled = true;
    }

    startButton.addEventListener('click', start);
    stopButton.addEventListener('click', stop);
    window.addEventListener('pagehide', stop);
    window.addEventListener('beforeunload', stop);
}
