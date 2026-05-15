export function addOfficialZonesLayers(map, geoJsonString) {
    if (!geoJsonString) return;
    map.addSource('official-zones', { type: 'geojson', data: JSON.parse(geoJsonString) });
    map.addLayer({ id: 'official-zones-fill', type: 'fill', source: 'official-zones', paint: { 'fill-color': '#555555', 'fill-opacity': 0.12 } });
    map.addLayer({ id: 'official-zones-line', type: 'line', source: 'official-zones', paint: { 'line-color': '#555555', 'line-width': 1.5 } });
    map.addLayer({
        id: 'official-zones-labels', type: 'symbol', source: 'official-zones',
        layout: { 'text-field': ['get', 'name'], 'text-size': 12, 'text-anchor': 'center', 'text-allow-overlap': false },
        paint: { 'text-color': '#333333', 'text-halo-color': '#ffffff', 'text-halo-width': 2 },
    });
}
