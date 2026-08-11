// Server-side values injected via data-* attributes on #map, plus static constants.
import { ESRI_TILES, MAP_BOUNDS } from '../shared/map-constants.js';

const el = document.getElementById('map');

export const CONFIG = {
    USER_CAMP_SEASON_ID: el.dataset.userCampSeasonId,
    IS_PLACEMENT_OPEN:   el.dataset.isPlacementOpen === 'true',
    IS_MAP_ADMIN:        el.dataset.isMapAdmin === 'true',

    ESRI_TILES,
    MAP_BOUNDS, // [SW, NE] corners of festival site
};
