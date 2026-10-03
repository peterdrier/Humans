// Server-side values injected via data-* attributes on #map.
import { ESRI_TILES, MAP_BOUNDS } from '../shared/map-constants.js';

const el = document.getElementById('map');

export const CONFIG = {
    YEAR:                parseInt(el.dataset.year, 10),
    IS_MAP_ADMIN:        el.dataset.isMapAdmin === 'true',
    USER_CAMP_ID:        el.dataset.userCampId || null,

    I18N: {
        SAVE_FAILED: el.dataset.i18nSaveFailed,
        CLEAR_FAILED: el.dataset.i18nClearFailed,
        CENTER_ON_CONTAINER: el.dataset.i18nCenterOnContainer,
        PLACEMENT_NOTES: el.dataset.i18nPlacementNotes,
        CLEAR_PLACEMENT: el.dataset.i18nClearPlacement,
        CONFIRM_CLEAR:   el.dataset.i18nConfirmClear,  // contains a {0} container-name placeholder
    },

    ESRI_TILES,
    MAP_BOUNDS,
};
