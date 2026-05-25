// SignalR hub connection and real-time event handlers.
import { appState } from './state.js';
import { CONFIG } from './config.js';
import { buildCampPolygonFeatures, invalidateParsedFeature } from './geometry.js';
import { updateAddMyBarrioVisibility } from './edit.js';

export function initSignalR() {
    const { map } = appState;

    appState.connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/city-planning')
        .withAutomaticReconnect()
        .build();

    appState.connection.on('CampPolygonUpdated', (campSeasonId, geoJson, areaSqm, soundZone, campName) => {
        const idx = appState.campMap.campPolygons.findIndex(p => p.campSeasonId === campSeasonId);
        if (idx >= 0) {
            appState.campMap.campPolygons[idx].geoJson = geoJson;
            appState.campMap.campPolygons[idx].areaSqm = areaSqm;
            invalidateParsedFeature(appState.campMap.campPolygons[idx]);
            // soundZone and campName are CampSeason properties — they don't change on polygon save
        } else {
            appState.campMap.campPolygons.push({ campSeasonId, geoJson, areaSqm, soundZone: soundZone ?? -1, campName: campName ?? '', campSlug: '' });
        }
        const src = map.getSource('camp-polygons');
        if (src) {
            src.setData({ type: 'FeatureCollection', features: buildCampPolygonFeatures(appState.campMap.campPolygons) });
        }
        updateAddMyBarrioVisibility();
    });

    appState.connection.on('CursorMoved', (connectionId, userName, lat, lng) => {
        if (!CONFIG.IS_PLACEMENT_OPEN) return;
        if (!appState.remoteCursors[connectionId]) {
            const el = document.createElement('div');
            el.className = 'remote-cursor';
            el.textContent = userName;
            appState.remoteCursors[connectionId] = new maplibregl.Marker({ element: el, anchor: 'top-left' })
                .setLngLat([lng, lat]).addTo(map);
        } else {
            appState.remoteCursors[connectionId].setLngLat([lng, lat]);
        }
    });

    appState.connection.on('CursorLeft', connectionId => {
        appState.remoteCursors[connectionId]?.remove();
        delete appState.remoteCursors[connectionId];
    });

    appState.connection.start().catch(console.error);

    if (CONFIG.IS_PLACEMENT_OPEN) {
        // Throttle to ~15Hz — mousemove fires at 60-120Hz, but the cursor
        // marker only needs to feel live to humans on the other end. With N
        // peers, cursor fanout is O(N²); the throttle puts a real ceiling on it.
        const CURSOR_INTERVAL_MS = 66;
        let lastSentAt = 0;
        let pendingLngLat = null;
        let scheduled = false;

        const send = () => {
            scheduled = false;
            if (!pendingLngLat) return;
            const { lat, lng } = pendingLngLat;
            pendingLngLat = null;
            lastSentAt = performance.now();
            if (appState.connection.state === signalR.HubConnectionState.Connected) {
                appState.connection.invoke('UpdateCursor', lat, lng).catch(() => {});
            }
        };

        map.on('mousemove', e => {
            pendingLngLat = e.lngLat;
            const elapsed = performance.now() - lastSentAt;
            if (elapsed >= CURSOR_INTERVAL_MS) {
                send();
            } else if (!scheduled) {
                scheduled = true;
                setTimeout(send, CURSOR_INTERVAL_MS - elapsed);
            }
        });
    }
}
