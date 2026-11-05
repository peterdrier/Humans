import { CONFIG } from './config.js';

export async function loadContainers(year) {
    const res = await fetch(`/api/city-planning/containers/${year}`);
    if (!res.ok) throw new Error(`Failed to load containers: ${res.status}`);
    return res.json();
}

export async function savePlacement(id, year, geoJson) {
    const res = await Humans.postJson(`/api/city-planning/containers/${id}/placement/${year}`, { geoJson }, { method: 'PUT' });
    if (!res.ok) {
        const text = await res.text().catch(() => res.statusText);
        throw new Error(`${CONFIG.I18N.SAVE_FAILED} (${res.status}): ${text}`);
    }
    return res.json();
}

export async function updatePlacementNotes(id, year, formData) {
    const res = await Humans.postForm(`/api/city-planning/containers/${id}/placement/${year}/notes`, formData, { method: 'PUT' });
    if (!res.ok) {
        const text = await res.text().catch(() => res.statusText);
        throw new Error(`${CONFIG.I18N.SAVE_FAILED} (${res.status}): ${text}`);
    }
    return res.json();
}

export async function clearPlacement(id, year) {
    const res = await fetch(`/api/city-planning/containers/${id}/placement/${year}`, {
        method: 'DELETE',
        headers: { 'RequestVerificationToken': Humans.csrfToken() },
    });
    if (!res.ok) {
        const text = await res.text().catch(() => res.statusText);
        throw new Error(`${CONFIG.I18N.CLEAR_FAILED} (${res.status}): ${text}`);
    }
}
