Process the freshness:auto block with id="entity-index" in
docs/architecture/data-model.md. The inline marker prompt is the
authoritative instruction; this file exists only because the marker prompt
duplicates with the catalog entry — the skill should prefer the inline
marker.

If the inline marker is missing or malformed, fall back to: regenerate the
"## Entity index" table by walking src/Humans.Domain/Entities/ and matching
to docs/sections/ owning sections, using columns Entity | Owning section | Notes.
