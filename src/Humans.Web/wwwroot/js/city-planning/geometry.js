// Pure spatial helpers. Depend on turf (global) and shared state for context.
import { appState } from './state.js';
import { CONFIG } from './config.js';

export function isOutsideZone(feature) {
  if (!appState.limitZoneGeom) return false;
  try { return !!turf.difference(turf.featureCollection([feature, appState.limitZoneGeom])); } catch { return false; }
}

const SOUND_ZONE_NAMES = { 0: 'blue', 1: 'green', 2: 'yellow', 3: 'orange', 4: 'red' };
// 5 means "surprise", which is OK for all sound zones

export function getSoundZoneOutOfRange(feature, campSoundZone) {
    if (campSoundZone === undefined || campSoundZone === null || campSoundZone === -1 || campSoundZone === 5) return false;
    const campZoneName = SOUND_ZONE_NAMES[campSoundZone];
    if (!campZoneName) return false;
    if (!appState.campMap?.limitZoneGeoJson) return false;
    let limitZoneData;
    try { limitZoneData = JSON.parse(appState.campMap.limitZoneGeoJson); } catch { return false; }
    const features = limitZoneData.type === 'FeatureCollection' ? limitZoneData.features : [limitZoneData];
    const centroid = turf.centroid(feature);
    for (const zf of features) {
        if (!zf.properties?.SoundZone) continue;
        try {
            if (turf.booleanPointInPolygon(centroid, zf)) {
                return !zf.properties.SoundZone.split('_').includes(campZoneName);
            }
        } catch { /* ignore */ }
    }
    return false;
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
        const spaceReq = p.spaceRequirementSqm ?? null;
        const spaceOutOfRange = spaceReq && p.areaSqm
            ? (p.areaSqm > spaceReq * 1.5 || p.areaSqm < spaceReq * 0.5)
            : false;
        const soundZoneVal = (p.soundZone !== undefined && p.soundZone !== null) ? p.soundZone : -1;
        f.properties = Object.assign(f.properties || {}, {
            campSeasonId:        p.campSeasonId,
            campName:            p.campName,
            areaSqm:             p.areaSqm,
            isOwn:               p.campSeasonId === CONFIG.USER_CAMP_SEASON_ID,
            soundZone:           soundZoneVal,
            outsideZone:         isOutsideZone(f),
            overlaps:            false,
            spaceRequirementSqm: spaceReq,
            spaceOutOfRange:     spaceOutOfRange,
            soundZoneOutOfRange: getSoundZoneOutOfRange(f, soundZoneVal),
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
