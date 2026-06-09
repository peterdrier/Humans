import { initBarcodeScanner } from './barcode.js';

export function initTicketScanner(refs) {
    const { card, cardUrl, ...scannerRefs } = refs;
    initBarcodeScanner({
        ...scannerRefs,
        results: null,
        resultsEmpty: null,
        onHit: async (value) => {
            try {
                const resp = await fetch(`${cardUrl}?barcode=${encodeURIComponent(value)}`,
                    { headers: { 'X-Requested-With': 'fetch' } });
                if (resp.ok) card.innerHTML = await resp.text();
            } catch (err) {
                console.error('Scanner: ticket lookup failed', err);
            }
        },
    });
}
