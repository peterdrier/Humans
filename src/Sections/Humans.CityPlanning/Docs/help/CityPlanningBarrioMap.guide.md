## Barrio Placement Map

The barrio placement map is the interactive editor where camp leads draw their barrio's footprint on the festival site. Each camp gets one polygon per year.

### Placement Phase

Editing is controlled by the **placement phase**, opened and closed by a Map Admin. Barrio leads can only draw or edit their own polygon while the phase is **open**. Map Admins can edit any polygon at any time.

Organisers can set dates showing when placement is expected to open and close, but the phase itself still opens and closes only when a Map Admin does it by hand.

### Drawing Your Barrio

1. Wait for placement to open (or check the schedule shown on the map)
2. Click **Add my barrio** — the map enters draw mode
3. Click points to trace the outline of your camp's footprint
4. Click **Save** to commit, or **Cancel** to discard

Only the **Barrio Lead** of a camp can edit that camp's polygon.

### Editing an Existing Polygon

Click your barrio's polygon on the map. A popup appears with an **Edit** button (visible when placement is open or you are a Map Admin). Click it to enter edit mode — drag vertices to reshape, or drag an edge midpoint to add a new vertex. Click **Save** to commit.

### Polygon History

Every save is recorded in an append-only history. Click **History** in the polygon popup to browse past versions. Map Admins can restore a previous version — this saves that old version again as the current one, noted with the time it was restored from. Nothing is lost, since every version is already recorded in history when it's saved.

### Measure Tool

Click the **Measure** button (ruler icon) to enter measurement mode, then click two points on the map to record a distance. Measure mode exits automatically once a measurement is recorded — click **Measure** again to add another. **Right-click** an existing measurement to delete it. Use the trash button next to **Measure** to clear all measurements at once. Entering measure mode exits edit mode if active.

### For Map Admins

**Map Admin** is not a standalone role. You are a Map Admin if you hold the `CampAdmin` role **or** belong to the `city-planning` team. Map Admins can edit any polygon at any time (regardless of placement phase), open/close placement, upload overlays, and export GeoJSON.

Open and close placement, and set the announced placement dates, from the City Planning tab in Settings.

The Admin panel provides:

- **Limit zone** — upload a GeoJSON outline of the site boundary (for visual reference)
- **Official zones** — upload a GeoJSON layer of pre-defined zones (for visual reference)
- **Containers** — manage shipping containers for each barrio
- **Export** — download the year's polygons as a GeoJSON file

### What Happens Automatically

- Every polygon save writes a history entry for audit
- Every save broadcasts a real-time update to all connected humans via SignalR
