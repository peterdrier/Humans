Process the freshness:auto block with id="entity-index" in
docs/architecture/data-model.md. The inline marker prompt is the
authoritative instruction; this file exists only because the marker prompt
duplicates with the catalog entry — the skill should prefer the inline
marker.

If the inline marker is missing or malformed, fall back to: regenerate the
"## Entity index" table by walking src/Humans.Domain/Entities/ **and
src/Sections/*/Domain/** — G5 sections own their entities outright, so a walk
restricted to Humans.Domain misses most of them — matching each to its owning
section doc (in `docs/sections/` or the section's own `Docs/`), using columns
Entity | Owning section | Notes.
