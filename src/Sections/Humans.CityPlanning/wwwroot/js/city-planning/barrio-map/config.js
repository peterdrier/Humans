// Server-side values injected via data-* attributes on #map, plus static constants.
import { ESRI_TILES, MAP_BOUNDS } from '../shared/map-constants.js';

const el = document.getElementById('map');

export const CONFIG = {
    USER_CAMP_SEASON_ID: el.dataset.userCampSeasonId,
    IS_PLACEMENT_OPEN:   el.dataset.isPlacementOpen === 'true',
    IS_MAP_ADMIN:        el.dataset.isMapAdmin === 'true',

    SAVE_FAILED: el.dataset.saveFailed,
    DISCARD_CONFIRM: el.dataset.discardConfirm,
    HISTORY_FOR_CAMP: el.dataset.historyForCamp,
    HISTORY_LOAD_FAILED: el.dataset.historyLoadFailed,
    HISTORY_EMPTY: el.dataset.historyEmpty,
    OUTSIDE_LIMITS: el.dataset.outsideLimits,
    OVERLAPS_BARRIO: el.dataset.overlapsBarrio,
    AREA_LARGER: el.dataset.areaLarger,
    AREA_SMALLER: el.dataset.areaSmaller,
    AREA_MUCH_LARGER: el.dataset.areaMuchLarger,
    AREA_MUCH_SMALLER: el.dataset.areaMuchSmaller,
    SOUND_ZONE_MISMATCH: el.dataset.soundZoneMismatch,
    GENERIC_CAMP: el.dataset.genericCamp,
    EDIT: el.dataset.edit,
    PREVIEW: el.dataset.preview,
    HISTORY: el.dataset.history,

    ESRI_TILES,
    MAP_BOUNDS, // [SW, NE] corners of festival site
};
