---
name: reforge.surface-score.json carries policy, never structure
description: When editing the root `reforge.surface-score.json` — keep `classifications` and per-section policy (DTO anchors, requires* overrides, grandfathered deps, escape hatches); never add a `sections` block listing paths, symbols, or the interfaces a section owns. Sections are assembly-derived.
type: rule
---

Put in `reforge.surface-score.json` only what the compiler cannot state. Never restate structure: a section's paths, its symbol prefixes, or which interfaces and repositories it owns.

**Why:** Sections are assembly-derived — `Humans.Camps` is the Camps section because that is what the assembly is called, with `Humans.Camps.Contracts` folded in. A `sections` block naming paths and symbols is a maintained list keyed by section name, the shape this project has already decided is dead, and reforge does not read those keys: they land in `[JsonExtensionData]` and are dropped, so the file can be wrong for months without one warning. The 33-section block deleted in PR #1421 changed nothing in the score (19,863 before and after) while still naming Admin, Dashboard and Platform, which had stopped being assemblies.

**How to apply:** Before adding a key, ask whether reforge could derive it from the solution. Assembly names, file locations, `I*Repository` naming — derivable, so leave them out. Which DTO is a section's canonical read shape, or a cross-section dependency we have decided to tolerate — not derivable, so config is the right home: `primaryInfoDto`, `settingsInfoDto`, `cacheDto`, `readShards`, `requiresReadSurface` / `requiresWriteSurface` / `requiresPrimaryInfoDto`, `grandfatheredDependencies`, `escapeHatchReadMethods`. `classifications` stays for the same reason — `*Row` being a DTO here is a convention, not a compiler fact — and merges per key with reforge's defaults, so a key we do not declare keeps the default rather than switching off.

**Exceptions:** None for structure. Per-section policy is welcome, one key at a time for the one section that needs it.

**Related:** `architecture/analyzer-exceptions-via-attributes.md` — same posture about per-violator lists. If a stale key does creep back, `surface-score` reports `unknown-config-section` (a block matching no assembly) and `removed-config-field` (a key no longer read); both mean delete, not update.
