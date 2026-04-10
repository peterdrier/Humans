// Pure spatial helpers. Depend on turf (global) and shared state for context.
import { appState } from './state.js';
import { CONFIG } from './config.js';

export function isOutsideZone(feature) {
  if (!appState.limitZoneGeom) return false;
  try { return !!turf.difference(turf.featureCollection([feature, appState.limitZoneGeom])); } catch { return false; }
}

export function parseLimitZoneGeom(geoJson) {
    if (!geoJson) return null;
    const lz = JSON.parse(geoJson);
    if (lz.type === 'FeatureCollection') {
        if (lz.features.length === 0) return null;
        if (lz.features.length === 1) return lz.features[0];
        return turf.union(lz);
    }
    return lz;
}

export function buildCampPolygonFeatures(campPolygons) {
    const features = campPolygons.map(p => {
        const f = JSON.parse(p.geoJson);
        f.properties = Object.assign(f.properties || {}, {
            campSeasonId: p.campSeasonId,
            campName:     p.campName,
            areaSqm:      p.areaSqm,
            isOwn:        p.campSeasonId === CONFIG.USER_CAMP_SEASON_ID,
            soundZone:    (p.soundZone !== undefined && p.soundZone !== null) ? p.soundZone : -1,
            outsideZone:  isOutsideZone(f),
            overlaps:     false,
        });
        return f;
    });

    // Pairwise overlap detection
    for (let i = 0; i < features.length; i++) {
        for (let j = i + 1; j < features.length; j++) {
            try {
                if (turf.intersect(turf.featureCollection([features[i], features[j]]))) {
                    features[i].properties.overlaps = true;
                    features[j].properties.overlaps = true;
                }
            } catch { /* ignore geometry errors */ }
        }
    }
    return features;
}

export function overlapsOtherCamps(feature) {
    const excludeId = appState.activeCampSeasonId ?? appState.previewCampSeasonId;
    return appState.campMap.campPolygons
        .filter(p => p.campSeasonId !== excludeId)
        .some(p => {
            try { return !!turf.intersect(turf.featureCollection([feature, JSON.parse(p.geoJson)])); }
            catch { return false; }
        });
}
