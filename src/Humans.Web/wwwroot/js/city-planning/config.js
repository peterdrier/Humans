import { ESRI_TILES, MAP_BOUNDS } from './shared/map-constants.js';

const el = document.getElementById('map');

export const CONFIG = {
    ESRI_TILES,
    MAP_BOUNDS,
    YEAR: parseInt(el.dataset.year, 10),
};
