# Prose & surface

Runs as a background command plus a `haiku` subagent.

**Two documents, two jobs — never judge one by the other's shape.** `Docs/health.md` is the
run's derived target shape (six parts plus the load-bearing weirdness list, 3c), and its
`file:line` cites point at the lines that *enforce* an invariant, not at declarations.
`Docs/<Section>.md` is the section's invariant doc, following
`docs/sections/SECTION-TEMPLATE.md`; feature specs cite it, not `health.md`, as the invariant
doc. Neither is restructured into the other's form.

InspectCode Tier 1/2; docs that are 500 words where 50 would do; missing translations; resource
keys not prefixed with the section name (`resource-key-prefix`, cleanup — report the count,
don't backfill unless the run is *for* that); nav quality — dead ends, missing backlinks,
discoverability from `AdminNavTree`.

**Fixed step, not judgment:** list every key in the section's base resx, grep each literal
across `*.cs`, `*.cshtml` and `*.resx` outside the set, exclude prefixes the code builds at
runtime (`$"…"` or concatenation against the localizer), and report the zero-reference
remainder as one finding with the list in `$RUNDIR`.
