import { CONFIG } from './config.js';

async function placementResponse(request, failureMessage, expectJson = true) {
    let res;
    try {
        res = await request;
    } catch {
        throw new Error(failureMessage);
    }
    if (!res.ok) {
        const text = await res.text().catch(() => res.statusText);
        throw new Error(`${failureMessage} (${res.status}): ${text}`);
    }
    if (!expectJson) return;
    try {
        return await res.json();
    } catch {
        throw new Error(failureMessage);
    }
}

export async function loadContainers(year) {
    const res = await fetch(`/api/city-planning/containers/${year}`);
    if (!res.ok) throw new Error(`Failed to load containers: ${res.status}`);
    return res.json();
}

export async function savePlacement(id, year, geoJson) {
    return placementResponse(
        Humans.postJson(`/api/city-planning/containers/${id}/placement/${year}`, { geoJson }, { method: 'PUT' }),
        CONFIG.I18N.SAVE_FAILED);
}

export async function updatePlacementNotes(id, year, formData) {
    return placementResponse(
        Humans.postForm(`/api/city-planning/containers/${id}/placement/${year}/notes`, formData, { method: 'PUT' }),
        CONFIG.I18N.SAVE_FAILED);
}

export async function clearPlacement(id, year) {
    return placementResponse(fetch(`/api/city-planning/containers/${id}/placement/${year}`, {
        method: 'DELETE',
        headers: { 'RequestVerificationToken': Humans.csrfToken() },
    }), CONFIG.I18N.CLEAR_FAILED, false);
}
