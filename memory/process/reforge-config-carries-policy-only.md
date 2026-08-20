---
name: reforge.surface-score.json carries policy, never structure
description: The root `reforge.surface-score.json` holds only what the compiler cannot state — classification name patterns, DTO anchors, requirement overrides, grandfathered dependencies, escape hatches. Sections, their paths, their symbol prefixes, and which interfaces they own are derived from assembly membership; never restate them in config.
type: rule
---

Sections are **assembly-derived**. `Humans.Camps` is the Camps section because that is what the assembly is called, with `Humans.Camps.Contracts` folded in as its published face. Nothing in config decides that, and nothing in config can change it.

So the config file holds only what the compiler cannot state:

- `classifications` — name patterns, paths, namespaces, base types and attributes that say what *kind* a type is (`dto`, `readServiceInterface`, `fullServiceInterface`, `repositoryInterface`, …). Genuinely policy: `*Row` being a DTO here is a project convention, not a compiler fact. Merged per key with reforge's defaults, so a key we do not declare keeps the default rather than switching off — `controllerAction`, `applicationServiceMethod` and `backgroundJob` all score without our declaring them.
- Per-section **policy**, when we need it: `primaryInfoDto`, `settingsInfoDto`, `cacheDto`, `readShards`, `requiresReadSurface` / `requiresWriteSurface` / `requiresPrimaryInfoDto`, `grandfatheredDependencies`, `escapeHatchReadMethods`.

**Never structure.** A `sections` block listing `paths`, `symbols`, `serviceInterfaces`, `repositoryInterfaces` or `readServiceInterfaces` is a maintained list keyed by section name — the shape this project has decided is dead. It restates what the solution already says, drifts every time a section moves or is renamed, and reforge silently ignores all of it: unrecognised section keys land in `[JsonExtensionData]` and are dropped, so the file can be wrong for months without a single warning. The 33-section block removed in the PR that added this atom changed **nothing** in the score (19,863 before and after) while carrying three sections that no longer existed.

**How to tell whether a key you are about to add is policy or structure:** could reforge work it out from the solution alone? Assembly names, which project a file lives in, which interfaces a section declares, what a repository is called — all derivable, so leave them out. Which DTO is *the* canonical read shape for a section, or which cross-section dependency we have decided to tolerate — not derivable, so config is the right home.

**If a stale key does creep in,** reforge reports the two it knows about: `unknown-config-section` (a section policy block matching no assembly) and `removed-config-field` (a key that used to be read and no longer is). Both are warnings on every `surface-score` run, and both mean delete, not update.
