## Barrio Placement Glossary

| Term | Definition |
|------|-----------|
| **Human** | A member of Nobodies Collective. We say "humans", not "members" or "volunteers". |
| **Barrio** | A themed village or camp at the event. Same thing as a "camp" — we use "barrio" for the placed footprint on the map. |
| **Camp Polygon** | The polygon a barrio has drawn on the map to claim its physical footprint. One per camp per year. |
| **Barrio Lead** | The human responsible for a camp — the only non-admin who can edit that camp's polygon. |
| **Placement Phase** | The window during which barrio leads can draw or edit their polygons. Opened and closed by Map Admins. |
| **Placement Window / Dates** | Scheduled open and close times for the placement phase — the phase auto-opens/closes on these dates. |
| **Map Admin** | Effective role for city-planning administration. Granted by holding `CampAdmin` or being a member of the `city-planning` team. |
| **CampAdmin** | System role with full admin access to camps and city planning. |
| **Limit Zone** | A GeoJSON polygon showing the boundary of the event site, displayed on the map for reference. |
| **Official Zones** | A GeoJSON layer of pre-defined zones (e.g., quiet zones, sound stages) displayed as an overlay. |
| **GeoJSON** | The open standard file format used for map polygons. Used for imports (limit zone, official zones) and exports. |
| **Polygon History** | The append-only log of every save to a camp's polygon. Map Admins can view and restore previous versions. |
| **SignalR** | The real-time channel used to push polygon updates to all connected users simultaneously. |
