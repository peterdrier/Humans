---
name: Culture support via CultureCatalog / CultureCodeExtensions
description: Use the shared culture helpers for supported lists, ordering, default selection, and display labels. No per-view language dictionaries.
---

Culture support and display names must be centralized.

**Rule:**
- Use `CultureCatalog` and `CultureCodeExtensions` for supported culture lists, ordering, default document language selection, and display labels
- Do not create per-view language dictionaries or ad hoc language ordering logic when the shared helpers already cover the case

**Why:** Prevents inconsistent language labels and tab ordering across views.
