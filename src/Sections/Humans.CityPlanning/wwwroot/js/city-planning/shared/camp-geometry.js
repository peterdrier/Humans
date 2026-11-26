export function parseCampFeature(geoJson) {
    const parsed = JSON.parse(geoJson);
    return parsed.type === 'Feature'
        ? parsed
        : { type: 'Feature', properties: {}, geometry: parsed };
}
