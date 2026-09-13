# Prose & surface

Runs as a background command plus a `haiku` subagent.

InspectCode Tier 1/2; docs that are 500 words where 50 would do; missing translations; resource
keys not prefixed with the section name (`resource-key-prefix`, cleanup — report the count,
don't backfill unless the run is *for* that); nav quality — dead ends, missing backlinks,
discoverability from `AdminNavTree`.

**Fixed step, not judgment:** list every key in the section's base resx, grep each literal
across `*.cs`, `*.cshtml` and `*.resx` outside the set, exclude prefixes the code builds at
runtime (`$"…"` or concatenation against the localizer), and report the zero-reference
remainder as one finding with the list in `$RUNDIR`.
