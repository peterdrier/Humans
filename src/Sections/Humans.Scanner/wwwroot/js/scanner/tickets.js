import { initBarcodeScanner } from './barcode.js';

export function initTicketScanner(refs) {
    const { card, cardUrl, manualForm, manualInput, labels, ...scannerRefs } = refs;

    // A failed lookup has to say so: the card otherwise keeps the previous ticket on
    // screen, which at a door reads as a successful scan of the wrong person.
    const showLookupFailed = () => {
        const alert = document.createElement('div');
        alert.className = 'alert alert-danger';
        alert.setAttribute('role', 'alert');
        alert.textContent = labels.lookupFailed;
        card.replaceChildren(alert);
    };

    const lookup = async (value) => {
        try {
            const resp = await fetch(`${cardUrl}?barcode=${encodeURIComponent(value)}`);
            if (resp.ok) {
                card.innerHTML = await resp.text();
            } else {
                showLookupFailed();
            }
        } catch (err) {
            console.error('Scanner: ticket lookup failed', err);
            showLookupFailed();
        }
    };

    manualForm.addEventListener('submit', (event) => {
        event.preventDefault();
        const value = manualInput.value.trim();
        if (value) void lookup(value);
    });

    initBarcodeScanner({
        ...scannerRefs,
        labels,
        results: null,
        resultsEmpty: null,
        onHit: lookup,
    });
}
