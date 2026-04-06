// SignalR hub connection and real-time event handlers.
import { appState } from './state.js';
import { CONFIG } from './config.js';
import { buildCampPolygonFeatures } from './geometry.js';
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
        map.on('mousemove', e => {
            if (appState.connection.state === signalR.HubConnectionState.Connected) {
                appState.connection.invoke('UpdateCursor', e.lngLat.lat, e.lngLat.lng).catch(() => {});
            }
        });
    }
}
