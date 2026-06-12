import { initBarcodeScanner } from './barcode.js';

export function initTicketScanner(refs) {
    const { card, cardUrl, manualForm, manualInput, ...scannerRefs } = refs;

    const lookup = async (value) => {
        try {
            const resp = await fetch(`${cardUrl}?barcode=${encodeURIComponent(value)}`,
                { headers: { 'X-Requested-With': 'fetch' } });
            if (resp.ok) card.innerHTML = await resp.text();
        } catch (err) {
            console.error('Scanner: ticket lookup failed', err);
        }
    };

    manualForm.addEventListener('submit', (event) => {
        event.preventDefault();
        const value = manualInput.value.trim();
        if (value) void lookup(value);
    });

    initBarcodeScanner({
        ...scannerRefs,
        results: null,
        resultsEmpty: null,
        onHit: lookup,
    });
}
