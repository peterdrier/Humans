---
name: Per-event data is keyed by event id, never by year
description: When a table or setting belongs to one event (season, rota, guide entry, placement, order), key it by the Settings event id, never by a year.
---

Data that belongs to one event carries the `settings_event` id (`EventSettingsInfo.Id`), and "the current event" is `ISettingsService.GetActiveEventSettingsAsync`. Never key such data by a year, and never keep a section-local "current year" or "open seasons" list.

**Why:** Peter (2026-10-28): "the sections should all be keyed off event id going forwards.. year was sloppy". There can be several events in one year (a spring and a fall event), and a year-keyed section drifts out of sync with the event settings (Barrios showed a 2026 season of its own).

**How to apply:** A new per-event table gets an event-id column; a per-event knob lives in the section's own table keyed by event id. Existing year-keyed tables are debt being re-keyed (nullable event-id column plus a data migration mapping year to that year's single event); don't copy them. A year shown to people is derived from the event's gate opening date.
