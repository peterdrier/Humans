// Server-side values injected via data-* attributes on #map.
import { ESRI_TILES, MAP_BOUNDS } from '../shared/map-constants.js';

const el = document.getElementById('map');

export const CONFIG = {
    YEAR:                parseInt(el.dataset.year, 10),
    IS_MAP_ADMIN:        el.dataset.isMapAdmin === 'true',
    USER_CAMP_ID:        el.dataset.userCampId || null,

    ESRI_TILES,
    MAP_BOUNDS,
};
