import { appState } from './state.js';
import { CONFIG } from './config.js';
import { buildCampPolygonFeatures, invalidateParsedFeature } from './geometry.js';
import { updateAddControls } from './edit.js';

export function initSignalR() {
    const { map } = appState;

    appState.connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/city-planning')
        .withAutomaticReconnect()
        .build();

    let pendingRefresh = null;

    function updatePlacement(state, update) {
        const { campSeasonId, geoJson, areaSqm, soundZone, campName } = update;
        const polygon = state.campPolygons.find(p => p.campSeasonId === campSeasonId);
        if (polygon) {
            polygon.geoJson = geoJson;
            polygon.areaSqm = areaSqm;
            invalidateParsedFeature(polygon);
            // soundZone and campName are CampSeason properties — they don't change on polygon save
        } else {
            const season = state.campSeasonsWithoutPolygon?.find(s => s.campSeasonId === campSeasonId);
            state.campPolygons.push({
                ...season, campSeasonId, geoJson, areaSqm,
                soundZone: soundZone ?? -1, campName: campName ?? '', campSlug: season?.campSlug ?? '',
            });
        }
    }

    function renderPlacements() {
        const src = map.getSource('camp-polygons');
        if (src) {
            src.setData({ type: 'FeatureCollection', features: buildCampPolygonFeatures(appState.campMap.campPolygons) });
        }
        updateAddControls();
    }

    async function refreshPlacements() {
        const refresh = { updates: new Map() };
        pendingRefresh = refresh;
        try {
            const response = await fetch('/api/city-planning/state', { cache: 'no-store' });
            if (!response.ok) throw new Error(`Map refresh failed: ${response.status}`);
            const state = await response.json();
            if (pendingRefresh !== refresh) return;
            // A broadcast received during the request is newer than the returned snapshot.
            for (const update of refresh.updates.values()) updatePlacement(state, update);
            appState.campMap.campPolygons = state.campPolygons;
            appState.campMap.campSeasonsWithoutPolygon = state.campSeasonsWithoutPolygon;
            renderPlacements();
        } catch (error) {
            if (pendingRefresh !== refresh) return;
            console.error('Failed to refresh map placements:', error);
            showToast(CONFIG.MAP_REFRESH_FAILED, 'danger');
        } finally {
            if (pendingRefresh === refresh) pendingRefresh = null;
        }
    }

    appState.connection.on('CampPolygonUpdated', (campSeasonId, geoJson, areaSqm, soundZone, campName) => {
        const update = { campSeasonId, geoJson, areaSqm, soundZone, campName };
        pendingRefresh?.updates.set(campSeasonId, update);
        updatePlacement(appState.campMap, update);
        renderPlacements();
    });
    appState.connection.onreconnected(refreshPlacements);

    appState.connection.on('CursorMoved', (connectionId, userName, lat, lng) => {
        if (!CONFIG.IS_PLACEMENT_OPEN && !CONFIG.IS_PLACEMENT_CHANGEABLE) return;
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

    appState.connection.start().then(refreshPlacements).catch(console.error);

    if (CONFIG.IS_PLACEMENT_OPEN || CONFIG.IS_PLACEMENT_CHANGEABLE) {
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
