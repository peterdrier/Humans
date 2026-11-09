# Prose & surface

Runs as a background command plus a `haiku` subagent.

**Two documents, two jobs — never judge one by the other's shape.** `Docs/health.md` is the
run's derived target shape, and its `file:line` cites point at the lines that *enforce* an
invariant, not at declarations. `Docs/<Section>.md` is the section's invariant doc, following
`docs/sections/SECTION-TEMPLATE.md`; feature specs cite it, not `health.md`. Neither is
restructured into the other's form.

InspectCode Tier 1/2; docs that are 500 words where 50 would do; missing translations; resource
keys not prefixed with the section name (`memory/code/resource-key-prefix-matches-section.md` —
report, don't backfill unless the run is *for* that); nav quality — dead ends, missing backlinks,
discoverability from `AdminNavTree`.

**Dead resx keys — a fixed step, not judgment, one half per way a key is read.** Keys read through the generated
designer property (`<Section>Resource.Key`) are symbols: reforge's references query answers them,
cross-project callers included. Keys read as strings (`IStringLocalizer["Key"]`, tag-helper
attributes in `.cshtml`) are not: grep each literal across `*.cs`, `*.cshtml` and `*.resx` outside
the set, excluding prefixes the code builds at runtime. A dead-key finding says both halves ran
and lists the zero-reference remainder in `$RUNDIR`; one that ran only one half is `unverified`.
