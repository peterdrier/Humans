// Rideshare board: joinable offers as route lines, requests as pins, the destination as a
// marker. Config and every user-facing string arrive as data-* on #map (no inline scripts).
// Clicking a line or pin opens a popup whose button fills and shows the matching modal;
// the modal is a normal POST form, so no fetch/antiforgery plumbing lives here.

const OSM_TILES = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';
const COLOR_MINE = '#0d6efd';
const COLOR_OTHER = '#198754';
const COLOR_REQUEST = '#fd7e14';
const COLOR_DESTINATION = '#dc3545';

const el = document.getElementById('map');
if (el) init(el).catch(err => console.error('Rideshare board init failed:', err));

async function init(el) {
    const cfg = el.dataset;
    const i18n = key => cfg[`i18n${key}`] ?? key;

    const map = new maplibregl.Map({
        container: 'map',
        style: {
            version: 8,
            sources: { osm: { type: 'raster', tiles: [OSM_TILES], tileSize: 256, maxzoom: 19, attribution: '© OpenStreetMap contributors' } },
            layers: [{ id: 'osm', type: 'raster', source: 'osm' }],
        },
        center: [8, 46],
        zoom: 4,
    });
    map.addControl(new maplibregl.NavigationControl(), 'top-right');
    await new Promise(resolve => map.on('load', resolve));

    const collection = await fetch(cfg.boardUrl, { headers: { Accept: 'application/geo+json' } }).then(r => r.json());
    const features = collection.features ?? [];
    const byKind = kind => ({ type: 'FeatureCollection', features: features.filter(f => f.properties?.kind === kind) });
    const tripById = new Map(features.filter(f => f.properties?.kind === 'trip').map(f => [f.properties.id, f]));

    map.addSource('trips', { type: 'geojson', data: byKind('trip') });
    map.addSource('trip-starts', { type: 'geojson', data: byKind('tripStart') });
    map.addSource('requests', { type: 'geojson', data: byKind('request') });

    const lineColor = ['case', ['get', 'isMine'], COLOR_MINE, COLOR_OTHER];
    // Wide transparent line first: the click target for thin routes.
    map.addLayer({ id: 'trips-hit', type: 'line', source: 'trips', paint: { 'line-color': '#000', 'line-width': 16, 'line-opacity': 0 } });
    map.addLayer({ id: 'trips-line', type: 'line', source: 'trips', layout: { 'line-cap': 'round', 'line-join': 'round' }, paint: { 'line-color': lineColor, 'line-width': 3 } });
    map.addLayer({
        id: 'trip-starts', type: 'circle', source: 'trip-starts',
        paint: { 'circle-radius': 6, 'circle-color': '#fff', 'circle-stroke-width': 3, 'circle-stroke-color': COLOR_OTHER },
    });
    map.addLayer({
        id: 'requests', type: 'circle', source: 'requests',
        paint: { 'circle-radius': 8, 'circle-color': COLOR_REQUEST, 'circle-stroke-width': 2, 'circle-stroke-color': '#fff' },
    });

    const destination = features.find(f => f.properties?.kind === 'destination');
    if (destination) {
        new maplibregl.Marker({ color: COLOR_DESTINATION })
            .setLngLat(destination.geometry.coordinates)
            .setPopup(new maplibregl.Popup({ offset: 25 }).setHTML(`<strong>${esc(i18n('Destination'))}</strong><div>${esc(destination.properties.label)}</div>`))
            .addTo(map);
    }

    fitToFeatures(map, features);

    let popup = null;
    const showPopup = (lngLat, html, wire) => {
        if (popup) popup.remove();
        popup = new maplibregl.Popup({ maxWidth: '320px' }).setLngLat(lngLat).setHTML(html).addTo(map);
        wire?.(popup.getElement());
    };

    for (const layer of ['trips-hit', 'trip-starts', 'requests']) {
        map.on('mouseenter', layer, () => { map.getCanvas().style.cursor = 'pointer'; });
        map.on('mouseleave', layer, () => { map.getCanvas().style.cursor = ''; });
    }

    const onTripClick = e => {
        const props = e.features[0].properties;
        const trip = props.kind === 'trip' ? props : tripById.get(props.id)?.properties;
        if (!trip) return;
        showPopup(e.lngLat, tripPopupHtml(trip, i18n), root => {
            root.querySelector('.js-popup-interest')?.addEventListener('click', () => openInterest(trip.id, trip.seatsRemaining));
        });
    };
    map.on('click', 'trip-starts', onTripClick);
    map.on('click', 'trips-hit', e => {
        // Pins sit on top of lines; let the pin layers win when both are under the cursor.
        if (map.queryRenderedFeatures(e.point, { layers: ['requests', 'trip-starts'] }).length) return;
        onTripClick(e);
    });
    map.on('click', 'requests', e => {
        const props = e.features[0].properties;
        showPopup(e.lngLat, requestPopupHtml(props, i18n), root => {
            root.querySelector('.js-popup-take')?.addEventListener('click', () => openTake(props.id, props.partySize));
        });
    });

    // The accessible list under the map shares the modals.
    document.querySelectorAll('.js-interest-btn').forEach(btn =>
        btn.addEventListener('click', () => openInterest(btn.dataset.tripId, Number(btn.dataset.seatsRemaining))));
    document.querySelectorAll('.js-take-btn').forEach(btn =>
        btn.addEventListener('click', () => openTake(btn.dataset.requestId, Number(btn.dataset.partySize))));
}

function tripPopupHtml(t, i18n) {
    const rows = [
        `<div class="small">${esc(i18n('Departs'))} ${esc(t.departureDate)} · ${esc(fmt(i18n('Days'), t.durationDays))}</div>`,
        `<div class="small">${esc(t.vehicleType)} · ${esc(t.luggageCapacity)} · ${esc(t.costSharing)}${t.costNote ? ' — ' + esc(t.costNote) : ''}</div>`,
        t.willingToDetour ? `<div class="small">${esc(i18n('Detour'))}</div>` : '',
        t.restrictions ? `<div class="small text-muted">${esc(i18n('Restrictions'))}: ${esc(t.restrictions)}</div>` : '',
    ].join('');
    const action = t.isMine
        ? `<span class="badge bg-primary">${esc(i18n('YourOffer'))}</span>`
        : `<button type="button" class="btn btn-primary btn-sm js-popup-interest">${esc(i18n('Interested'))}</button>`;
    return `${personHtml(t.driverName, t.driverPictureUrl, i18n('Driver'))}
        <div><strong>${esc(t.placeLabel)}</strong></div>
        <div class="small"><span class="badge bg-success">${esc(fmt(i18n('SeatsLeft'), t.seatsRemaining, t.seatsOffered))}</span></div>
        ${rows}<div class="mt-2">${action}</div>`;
}

function requestPopupHtml(r, i18n) {
    const action = r.isMine
        ? `<span class="badge bg-primary">${esc(i18n('YourRequest'))}</span>`
        : `<button type="button" class="btn btn-outline-primary btn-sm js-popup-take">${esc(i18n('Take'))}</button>`;
    return `${personHtml(r.riderName, r.riderPictureUrl, i18n('Rider'))}
        <div><strong>${esc(r.placeLabel)}</strong> · ${esc(r.desiredDate)}</div>
        <div class="small">${esc(i18n('People'))}: ${esc(r.partySize)} · ${esc(r.luggageLoad)}${r.canContributeToFuel ? ' · ' + esc(i18n('Fuel')) : ''}</div>
        ${r.notes ? `<div class="small text-muted">${esc(r.notes)}</div>` : ''}
        <div class="mt-2">${action}</div>`;
}

function personHtml(name, pictureUrl, role) {
    const avatar = pictureUrl
        ? `<img src="${esc(pictureUrl)}" alt="" class="rounded-circle me-2" width="32" height="32" style="object-fit: cover;">`
        : `<span class="d-inline-flex align-items-center justify-content-center rounded-circle bg-secondary text-white me-2" style="width: 32px; height: 32px;">${esc((name || '?').charAt(0).toUpperCase())}</span>`;
    return `<div class="d-flex align-items-center mb-1">${avatar}<div><div class="fw-semibold">${esc(name)}</div><div class="small text-muted">${esc(role)}</div></div></div>`;
}

function openInterest(tripId, seatsRemaining) {
    document.getElementById('interestTripId').value = tripId;
    const seats = document.getElementById('interestSeats');
    seats.max = String(Math.max(1, seatsRemaining || 1));
    seats.value = '1';
    bootstrap.Modal.getOrCreateInstance(document.getElementById('interestModal')).show();
}

function openTake(requestId, partySize) {
    document.getElementById('takeRequestId').value = requestId;
    document.getElementById('takeSeats').value = String(Math.max(1, partySize || 1));
    bootstrap.Modal.getOrCreateInstance(document.getElementById('takeModal')).show();
}

function fitToFeatures(map, features) {
    const bounds = new maplibregl.LngLatBounds();
    let any = false;
    const walk = coords => {
        if (typeof coords[0] === 'number') { bounds.extend(coords); any = true; }
        else coords.forEach(walk);
    };
    features.forEach(f => f.geometry?.coordinates && walk(f.geometry.coordinates));
    if (any) map.fitBounds(bounds, { padding: 60, maxZoom: 10, duration: 0 });
}

// "{0}"-style placeholders from the resx, filled client-side.
function fmt(template, ...args) {
    return String(template).replace(/\{(\d+)\}/g, (_, i) => String(args[Number(i)] ?? ''));
}

function esc(value) {
    return String(value ?? '')
        .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;').replaceAll("'", '&#39;');
}
