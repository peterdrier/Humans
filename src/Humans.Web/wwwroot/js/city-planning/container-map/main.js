// Entry point for the container placement map.
import { CONFIG }                                          from './config.js';
import { loadContainers, savePlacement, clearPlacement }   from './api.js';
import { initPlacementNotes, openPlacementNotes }           from './placement-notes.js';
import { addBackgroundLayers, addContainerLayers, updateContainerSource } from './layers.js';
import { initSidebar, setContainers, setActiveId, markPlaced, scrollToPlaced } from './sidebar.js';
import { initInteraction, activateContainer, selectPlacedContainer, deactivate } from './interaction.js';
import { initMeasure, exitMeasureMode, wireMeasureButtons } from './measure.js';

const toast = document.getElementById('map-toast');
let containers = []; // live container data array

function showToast(msg) {
    toast.textContent = msg;
    toast.style.display = 'block';
    setTimeout(() => { toast.style.display = 'none'; }, 4000);
}

/** Update locationGeoJson for a container in the local array. */
function patchContainer(id, locationGeoJson) {
    const c = containers.find(x => x.id === id);
    if (c) c.locationGeoJson = locationGeoJson;
}

/** Apply notes-update result to the local array and refresh the sidebar. */
function applyPlacementNotesUpdate(id, fields) {
    const c = containers.find(x => x.id === id);
    if (!c) return;
    c.placementNotes = fields.placementNotes;
    c.placementImageUrl = fields.placementImageUrl;
    c.placementImageFileName = fields.placementImageFileName;
    setContainers(containers);
}

async function init() {
    const map = new maplibregl.Map({
        container: 'map',
        style: {
            version: 8,
            sources: { esri: { type: 'raster', tiles: [CONFIG.ESRI_TILES], tileSize: 256, maxzoom: 19, attribution: '© Esri' } },
            layers: [{ id: 'esri-layer', type: 'raster', source: 'esri' }],
        },
        bounds: CONFIG.MAP_BOUNDS,
    });

    await new Promise(resolve => map.on('load', resolve));

    // State + containers are independent — fetch in parallel.
    const [stateData, loadedContainers] = await Promise.all([
        fetch('/api/city-planning/state').then(r => r.json()),
        loadContainers(CONFIG.YEAR),
    ]);
    addBackgroundLayers(map, stateData);
    addContainerLayers(map);
    initMeasure(map);

    containers = loadedContainers;
    updateContainerSource(map, containers, null);

    // Zoom to barrio lead's camp if not admin
    if (!CONFIG.IS_MAP_ADMIN && CONFIG.USER_CAMP_ID) {
        const campPolygon = stateData.campPolygons?.find(p => p.campId === CONFIG.USER_CAMP_ID);
        if (campPolygon?.geoJson) {
            const bbox = turf.bbox(JSON.parse(campPolygon.geoJson));
            map.fitBounds([[bbox[0], bbox[1]], [bbox[2], bbox[3]]], { padding: 60, duration: 0 });
        }
    }

    wireMeasureButtons({
        beforeEnter: () => {
            deactivate();
            setActiveId(null);
            updateContainerSource(map, containers, null);
        },
    });

    initSidebar(
        // onActivate: user clicked an unplaced container card
        async (container) => {
            exitMeasureMode();
            deactivate();
            const center = getBarrioCenter(stateData, container);
            setActiveId(container.id);
            activateContainer(container, center.lng, center.lat);
            updateContainerSource(map, containers, container.id);
        },
        // onClear: user clicked "Clear placement"
        async (container) => {
            const confirmed = confirm(
                `Remove placement for «${container.name}»? This cannot be undone.`
            );
            if (!confirmed) return;
            try {
                await clearPlacement(container.id, CONFIG.YEAR);
                patchContainer(container.id, null);
                setContainers(containers);
                updateContainerSource(map, containers, null);
                deactivate();
            } catch (err) {
                showToast(err.message);
            }
        },
        // onSelect: user clicked a placed container card
        (container) => {
            exitMeasureMode();
            deactivate();
            setActiveId(container.id);
            selectPlacedContainer(container);
            updateContainerSource(map, containers, container.id);
        },
        // onLocate: user clicked the locate button on a placed card
        (container) => {
            const f = JSON.parse(container.locationGeoJson);
            map.flyTo({ center: [f.properties.center_lng, f.properties.center_lat], duration: 400 });
        },
        // onInfo: user clicked the placement-notes button on a placed card
        (container) => openPlacementNotes(container),
        CONFIG.IS_MAP_ADMIN ? null : CONFIG.USER_CAMP_ID || null,
    );
    setContainers(containers);

    initPlacementNotes(applyPlacementNotesUpdate);

    // Wire interaction
    initInteraction(
        map,
        // onSave: called on drag/rotate mouseup
        async (container, feature) => {
            try {
                const geoJson = JSON.stringify(feature);
                await savePlacement(container.id, CONFIG.YEAR, geoJson);
                patchContainer(container.id, geoJson);
                setContainers(containers);
                markPlaced(container.id);
                updateContainerSource(map, containers, container.id);
                scrollToPlaced(container.id);
            } catch (err) {
                showToast(err.message);
            }
        },
        // onSelect: user clicked a placed editable container on the map
        (featureId) => {
            const container = containers.find(c => c.id === featureId && c.canEdit);
            if (!container || !container.locationGeoJson) return;
            deactivate();
            setActiveId(container.id);
            selectPlacedContainer(container);
            updateContainerSource(map, containers, container.id);
            scrollToPlaced(container.id);
        },
    );
}

/**
 * Compute the placement center for a container being activated:
 * centroid of its camp polygon, or fallback to site center.
 */
function getBarrioCenter(stateData, container) {
    const siteCenterLng = (CONFIG.MAP_BOUNDS[0][0] + CONFIG.MAP_BOUNDS[1][0]) / 2;
    const siteCenterLat = (CONFIG.MAP_BOUNDS[0][1] + CONFIG.MAP_BOUNDS[1][1]) / 2;

    const campPolygon = stateData.campPolygons?.find(p => p.campId === container.campId);
    if (!campPolygon?.geoJson) {
        return { lng: siteCenterLng, lat: siteCenterLat };
    }

    const centroid = turf.centroid(JSON.parse(campPolygon.geoJson));
    return { lng: centroid.geometry.coordinates[0], lat: centroid.geometry.coordinates[1] };
}

init().catch(err => console.error('Container map init failed:', err));
