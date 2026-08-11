// Shared mutable state. All modules mutate properties on this object — never reassign the export.
export const appState = {
    map:               null,
    draw:              null,
    connection:        null,
    campMap:           null,   // fetched from /api/city-planning/state
    limitZoneGeom:     null,   // parsed turf geometry for isOutsideZone checks
    activeCampSeasonId:  null,  // non-null while a polygon is being edited
    previewCampSeasonId: null,  // non-null while previewing a historical version
    remoteCursors:     {},
    currentPopup:      null,
};
