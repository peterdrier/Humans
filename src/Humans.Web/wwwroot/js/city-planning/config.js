// Server-side values injected via data-* attributes on #map, plus static constants.
const el = document.getElementById('map');

export const CONFIG = {
    USER_CAMP_SEASON_ID: el.dataset.userCampSeasonId,
    IS_PLACEMENT_OPEN:   el.dataset.isPlacementOpen === 'true',
    IS_MAP_ADMIN:        el.dataset.isMapAdmin === 'true',

    ESRI_TILES: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
    MAP_BOUNDS: [
        [-0.14285979741055144, 41.696961407716145],
        [-0.13157837273621453, 41.70290716137069],
    ], // [SW, NE] corners of festival site
};
