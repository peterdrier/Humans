// MapLibre layer setup for the container placement map.
// addBackgroundLayers mirrors the read-only styling from city-planning/layers.js.
// addContainerLayers adds container-specific sources and layers.

/**
 * Adds read-only background layers: camp polygons and official zones.
 * stateData: response from GET /api/city-planning/state
 */
export function addBackgroundLayers(map, stateData) {
    const campFeatures = (stateData.campPolygons || [])
        .filter(p => p.geoJson)
        .map(p => {
            const f = JSON.parse(p.geoJson);
            if (!f.properties) f.properties = {};
            f.properties.campName  = p.campName;
            f.properties.soundZone = p.soundZone;
            return f;
        });

    map.addSource('camp-polygons', {
        type: 'geojson',
        data: { type: 'FeatureCollection', features: campFeatures },
    });

    map.addLayer({
        id: 'camp-polygons-fill', type: 'fill', source: 'camp-polygons',
        paint: {
            'fill-color': ['match', ['get', 'soundZone'],
                0, '#88aadd', 1, '#88bb88', 2, '#ddcc66', 3, '#ddaa66', 4, '#dd8888', '#aaaaaa',
            ],
            'fill-opacity': 0.2,
        },
    });

    map.addLayer({
        id: 'camp-polygons-outline', type: 'line', source: 'camp-polygons',
        paint: {
            'line-color': ['match', ['get', 'soundZone'],
                0, '#2266cc', 1, '#229944', 2, '#cc9900', 3, '#cc6600', 4, '#cc1111', 5, '#cc00cc', '#666666',
            ],
            'line-width': 1,
        },
    });

    map.addLayer({
        id: 'camp-polygons-labels', type: 'symbol', source: 'camp-polygons',
        layout: {
            'text-field': ['get', 'campName'],
            'text-size': 12,
            'text-anchor': 'center',
            'text-allow-overlap': false,
        },
        paint: { 'text-color': '#000000', 'text-halo-color': '#ffffff', 'text-halo-width': 2 },
    });

    if (stateData.officialZonesGeoJson) {
        map.addSource('official-zones', {
            type: 'geojson',
            data: JSON.parse(stateData.officialZonesGeoJson),
        });
        map.addLayer({
            id: 'official-zones-fill', type: 'fill', source: 'official-zones',
            paint: { 'fill-color': '#555555', 'fill-opacity': 0.12 },
        });
        map.addLayer({
            id: 'official-zones-line', type: 'line', source: 'official-zones',
            paint: { 'line-color': '#555555', 'line-width': 1.5 },
        });
        map.addLayer({
            id: 'official-zones-labels', type: 'symbol', source: 'official-zones',
            layout: {
                'text-field': ['get', 'name'],
                'text-size': 12,
                'text-anchor': 'center',
                'text-allow-overlap': false,
            },
            paint: { 'text-color': '#333333', 'text-halo-color': '#ffffff', 'text-halo-width': 2 },
        });
    }
}

/**
 * Adds container sources and layers. Sources start empty; call updateContainerSource()
 * and updateActiveSource() to populate them.
 */
export function addContainerLayers(map) {
    map.addSource('containers', {
        type: 'geojson',
        data: { type: 'FeatureCollection', features: [] },
    });

    map.addSource('container-active', {
        type: 'geojson',
        data: { type: 'FeatureCollection', features: [] },
    });

    // Read-only containers (canEdit = false)
    map.addLayer({
        id: 'containers-readonly-fill', type: 'fill', source: 'containers',
        filter: ['==', ['get', 'canEdit'], false],
        paint: { 'fill-color': '#e8a020', 'fill-opacity': 0.6 },
    });
    map.addLayer({
        id: 'containers-readonly-outline', type: 'line', source: 'containers',
        filter: ['==', ['get', 'canEdit'], false],
        paint: { 'line-color': '#c07010', 'line-width': 1.5 },
    });

    // Editable containers (canEdit = true, not active)
    map.addLayer({
        id: 'containers-editable-fill', type: 'fill', source: 'containers',
        filter: ['==', ['get', 'canEdit'], true],
        paint: { 'fill-color': '#e8a020', 'fill-opacity': 0.85 },
    });
    map.addLayer({
        id: 'containers-editable-outline', type: 'line', source: 'containers',
        filter: ['==', ['get', 'canEdit'], true],
        paint: { 'line-color': '#ffffff', 'line-width': 2 },
    });

    // Active container (separate source)
    map.addLayer({
        id: 'containers-active-fill', type: 'fill', source: 'container-active',
        paint: { 'fill-color': '#4fc3f7', 'fill-opacity': 0.9 },
    });
    map.addLayer({
        id: 'containers-active-outline', type: 'line', source: 'container-active',
        paint: { 'line-color': '#ffffff', 'line-width': 2.5 },
    });

    // Labels (shown at close zoom on non-active containers)
    map.addLayer({
        id: 'containers-labels', type: 'symbol', source: 'containers',
        minzoom: 17,
        layout: {
            'text-field': ['get', 'name'],
            'text-size': 10,
            'text-anchor': 'center',
            'text-allow-overlap': false,
        },
        paint: { 'text-color': '#ffffff', 'text-halo-color': '#000000', 'text-halo-width': 1 },
    });

    // Label on the active (selected/being-placed) container — always visible
    map.addLayer({
        id: 'containers-active-label', type: 'symbol', source: 'container-active',
        layout: {
            'text-field': ['get', 'name'],
            'text-size': 10,
            'text-anchor': 'center',
            'text-allow-overlap': true,
        },
        paint: { 'text-color': '#ffffff', 'text-halo-color': '#000000', 'text-halo-width': 1 },
    });
}

/**
 * Rebuilds the 'containers' source from the current containers array.
 * Only placed containers (locationGeoJson != null) appear in this source.
 * activeId: the currently active container ID, excluded from this source.
 */
export function updateContainerSource(map, containers, activeId) {
    const features = containers
        .filter(c => c.locationGeoJson && c.id !== activeId)
        .map(c => {
            const f = JSON.parse(c.locationGeoJson);
            if (!f.properties) f.properties = {};
            f.properties.id      = c.id;
            f.properties.name    = c.name;
            f.properties.canEdit = c.canEdit;
            return f;
        });
    map.getSource('containers').setData({ type: 'FeatureCollection', features });
}

/**
 * Updates the 'container-active' source to show the given feature (or clears it).
 */
export function updateActiveSource(map, feature) {
    const data = feature
        ? { type: 'FeatureCollection', features: [feature] }
        : { type: 'FeatureCollection', features: [] };
    map.getSource('container-active').setData(data);
}
