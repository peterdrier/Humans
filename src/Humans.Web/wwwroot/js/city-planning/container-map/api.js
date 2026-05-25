// API calls for container placement.

function antiforgeryToken() {
    return document.querySelector('input[name="__RequestVerificationToken"]').value;
}

export async function loadContainers(year) {
    const res = await fetch(`/api/city-planning/containers/${year}`);
    if (!res.ok) throw new Error(`Failed to load containers: ${res.status}`);
    return res.json();
}

export async function savePlacement(id, year, geoJson) {
    const res = await fetch(`/api/city-planning/containers/${id}/placement/${year}`, {
        method: 'PUT',
        headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': antiforgeryToken(),
        },
        body: JSON.stringify({ geoJson }),
    });
    if (!res.ok) {
        const text = await res.text().catch(() => res.statusText);
        throw new Error(`Save failed (${res.status}): ${text}`);
    }
    return res.json();
}

export async function updatePlacementNotes(id, year, formData) {
    formData.set('__RequestVerificationToken', antiforgeryToken());
    const res = await fetch(`/api/city-planning/containers/${id}/placement/${year}/notes`, {
        method: 'PUT',
        headers: { 'RequestVerificationToken': antiforgeryToken() },
        body: formData,
    });
    if (!res.ok) {
        const text = await res.text().catch(() => res.statusText);
        throw new Error(`Save failed (${res.status}): ${text}`);
    }
    return res.json();
}

export async function clearPlacement(id, year) {
    const res = await fetch(`/api/city-planning/containers/${id}/placement/${year}`, {
        method: 'DELETE',
        headers: { 'RequestVerificationToken': antiforgeryToken() },
    });
    if (!res.ok) {
        const text = await res.text().catch(() => res.statusText);
        throw new Error(`Clear failed (${res.status}): ${text}`);
    }
}
