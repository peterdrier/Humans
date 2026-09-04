// Coarse point picker for the offer/request forms: click the map to set the latitude and
// longitude inputs named by data-lat-input / data-lng-input; editing the inputs moves the marker.
// Retyping the place label (data-place-input) clears the pin so the server geocodes the new label
// instead of keeping stale coordinates.

const OSM_TILES = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';

const el = document.getElementById('pick-map');
if (el) init(el);

function init(el) {
    const latInput = document.getElementById(el.dataset.latInput);
    const lngInput = document.getElementById(el.dataset.lngInput);
    const initial = readPoint(latInput, lngInput);

    const map = new maplibregl.Map({
        container: el,
        style: {
            version: 8,
            sources: { osm: { type: 'raster', tiles: [OSM_TILES], tileSize: 256, maxzoom: 19, attribution: '© OpenStreetMap contributors' } },
            layers: [{ id: 'osm', type: 'raster', source: 'osm' }],
        },
        center: initial ? [initial.lng, initial.lat] : [8, 46],
        zoom: initial ? 8 : 4,
    });
    map.addControl(new maplibregl.NavigationControl(), 'top-right');

    let marker = null;
    const place = (lng, lat) => {
        if (!marker) marker = new maplibregl.Marker({ color: '#0d6efd' }).setLngLat([lng, lat]).addTo(map);
        else marker.setLngLat([lng, lat]);
    };
    if (initial) place(initial.lng, initial.lat);

    map.on('click', e => {
        const { lng, lat } = e.lngLat;
        latInput.value = lat.toFixed(5);
        lngInput.value = lng.toFixed(5);
        place(lng, lat);
    });

    const onEdit = () => {
        const p = readPoint(latInput, lngInput);
        if (p) { place(p.lng, p.lat); map.easeTo({ center: [p.lng, p.lat] }); }
    };
    latInput.addEventListener('change', onEdit);
    lngInput.addEventListener('change', onEdit);

    const placeInput = document.getElementById(el.dataset.placeInput);
    if (placeInput) {
        placeInput.addEventListener('change', () => {
            latInput.value = '';
            lngInput.value = '';
            if (marker) { marker.remove(); marker = null; }
        });
    }
}

function readPoint(latInput, lngInput) {
    const lat = parseFloat(latInput.value);
    const lng = parseFloat(lngInput.value);
    return Number.isFinite(lat) && Number.isFinite(lng) ? { lat, lng } : null;
}
