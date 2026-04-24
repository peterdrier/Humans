# SECTION-TEMPLATE — How to write a section invariants doc

Every file in `docs/sections/` describes one section of the app. Each doc serves **two readers**: a human (or AI) trying to understand the section's contract before changing it, and a reviewer checking a PR against that contract. Keep it terse, concrete, and authoritative — this is not a narrative, it is a set of invariants.

> **This file is a template, not a section.** When adding a new section: copy the canonical shape below into a new `SectionName.md`, fill in each required heading, and delete any optional headings you do not need. Keep the order of the required headings exactly as shown — reviewers rely on it.

---

## Why sections exist (and why this doc matters)

The codebase is organised by **section**, not by layer. A section is a vertical slice — entities, services, repositories, controllers, views, tests — that shares a single owner. The section docs are the **authoritative contract** for each slice.

Sections are the primary mechanism for keeping the app out of spaghetti as it grows. The enforcement story has three parts:

1. **`docs/architecture/design-rules.md`** defines the architectural laws that apply to every section (§8 table ownership, §2c no cross-service table access, §6 no cross-domain `.Include`, §11 auth pattern, §12 append-only entities, §15 caching pattern).
2. **`docs/sections/<Section>.md`** (this template) applies those laws to one section — who owns what, what invariants hold, which cross-section interfaces it uses, and where the section is in its migration to the §15 pattern.
3. **Architecture tests** (`tests/Humans.Application.Tests/Architecture/`) pin the rules at compile/test time so drift fails the build, not just code review.

If a change conflicts with the section doc, **either the change is wrong or the doc is** — both are PR-blocking until one is updated.

---

## Canonical shape

Copy everything between the two horizontal rules below into the new section file. Delete any **optional** heading you do not need; keep every **required** heading even if its bullet list is short.

---

```markdown
# <Section Name> — Section Invariants

<!-- One-sentence purpose of the section. Optional but helpful. -->

## Concepts

- A **<Primary Entity>** is ...
- ...

<!--
Required. Define the domain vocabulary this section owns. Entities, enums,
singletons, lifecycle terms ("Draft then Active then Closed"). Keep each bullet
to one or two sentences. Field-level detail belongs under `## Data Model` below.
-->

## Data Model

### <Primary Entity>

**Table:** `<table_name>`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ... | ... | ... |

**Indexes / constraints:** ...

**Cross-section FKs:** `<Field>` → `<OtherSection.Entity>` (Other Section) — **FK only**, no navigation property.

### <Enum owned by this section>

| Value | Int | Description |
|-------|-----|-------------|
| ... | ... | ... |

<!--
Required if the section owns any tables. One subsection per entity / enum /
value object this section OWNS. Field-level detail (types, defaults, indexes,
constraints, serialization rules) lives here, NOT in `docs/architecture/data-model.md`.

Rules:
1. One section owns each entity. If another section needs to read it, it calls
   the owning section's public service interface — never joins the table.
2. Cross-section FKs are scalar only; the navigation property MUST NOT be
   declared on the entity. If legacy navs still exist, note them under
   Architecture → Migration status (they are technical debt, not data model).
3. Enums owned by this section live here too. Enums used across many sections
   (e.g. AuditAction) live in `docs/architecture/data-model.md` only if they
   genuinely have no single owner.
4. If the entity is append-only per design-rules §12, say so and name the
   enforcement (DB trigger, repository shape, architecture test).
5. Do NOT duplicate fields into `docs/architecture/data-model.md` — that file
   is an index and cross-cutting rule sheet, not a second source of truth.
-->

## Routing

<!--
Optional. Include ONLY when the URL-to-controller mapping is non-obvious
(Profiles: /Profile/Me vs /Profile/{id}, Google: /Google vs /Teams/{slug}/Resources)
or when multiple controllers serve one section. If the section has a single
conventional controller, skip this heading entirely.
-->

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | ... |
| <Role> | ... |
| Admin | ... |

<!--
Required. One row per distinct capability bucket. Combine roles on the same
row when they share capabilities ("HumanAdmin, Board, Admin"). Do NOT restate
capabilities for a more-privileged row — use "All <lower role> capabilities.
Additionally ..." so the inheritance is visible.
-->

## Invariants

- <hard rule that must always be true>
- ...

<!--
Required. The rules the section ALWAYS enforces, at any layer. These are the
statements a reviewer checks a PR against. State them as present-tense facts,
not aspirations. If a rule has enforcement (DB trigger, architecture test,
authorization handler), mention it parenthetically so reviewers know where the
guardrail lives.
-->

## Negative Access Rules

- <actor> **cannot** <action> ...
- ...

<!--
Required. The explicit "cannot" list. Redundant with Invariants in theory,
but essential in practice: the affirmative list omits privileges silently; the
negative list makes refusals explicit and greppable. Always bold **cannot**.
-->

## Triggers

- When <event>, <side effect> ...
- ...

<!--
Required. Side effects and cascades — "when X happens, Y must happen." Covers
audit writes, notification fan-out, cross-section reconciliation, background
jobs, email sends. If this section owns zero triggers, write "None — this
section is a pure read/write surface with no side effects." (rare).
-->

## Cross-Section Dependencies

- **<Other Section>**: <what this section reads or calls via which interface>
- ...

<!--
Required. One bullet per other section this one talks to, naming the
dependency direction and the interface used. Concrete is better than abstract:
"calls `ITeamService.GetByIdsAsync`" beats "integrates with Teams." Required
whether the dependency is strong (shared writes) or weak (display labels only).
-->

## Architecture

**Owning services:** `<ServiceA>`, `<ServiceB>`
**Owned tables:** `<table_a>`, `<table_b>`
**Status:** <(A) Migrated | (B) Partially migrated | (C) Pre-migration>  —  <one-line summary with issue/PR + date>

<!--
Required. The three-line footer. Owning services and owned tables must stay in
sync with `docs/architecture/design-rules.md §8`. If the section owns no
tables, write "None — orchestrator over <list of services>." If this section
gains or loses a table, UPDATE §8 IN THE SAME COMMIT (CLAUDE.md coding rules).

Status is one of A/B/C; the sub-sections below depend on which one you pick.
Delete the two blocks that do not apply.
-->

### For (A) Migrated sections

Use this single block; delete the (B) and (C) blocks below.

- Service(s) live in `Humans.Application.Services.<Section>/` and never import `Microsoft.EntityFrameworkCore`.
- `I<Section>Repository` (impl in `Humans.Infrastructure/Repositories/`) is the only code path that touches this section's tables via `DbContext`.
- **Decorator decision** — one of:
  - Caching decorator (`Caching<Section>Service`, Singleton, dict-backed). Pattern per design-rules §15d. Warmup via `<Name>WarmupHostedService`.
  - No caching decorator. Rationale: <low-traffic, admin-only, sequential queue drain, etc.>
- **Cross-domain navs** — stripped or `[Obsolete]`-marked: <list>. Display stitching routes through `<IUserService.GetByIdsAsync, ITeamService.GetTeamNamesByIdsAsync, …>`.
- **Cross-section calls** — the public interfaces this section consumes: `<IUserService, ITeamService, ...>`.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/<Section>ArchitectureTests.cs` pins the shape.

Optional appendix blocks (add only when genuinely useful; omit otherwise):

- `### Repository surface` — the narrow list of repo methods with one-line purpose each (use when the shape is non-obvious, e.g. CityPlanning's atomic "polygon + history" write).
- `### Touch-and-clean guidance` — residual items a future PR should clean up (cross-cutting User-nav strip, stale `#pragma warning disable CS0618`, etc.). Keep to file:line references.

### For (B) Partially migrated sections

Use all three sub-blocks below; delete the (A) and (C) blocks.

#### Target repositories

- **`I<Section>Repository`** — owns <tables>
  - Aggregate-local navs kept: <list>
  - Cross-domain navs stripped: <list with section of origin>
  - Append-only note (if §12 applies): ...

#### Current violations

<!--
Keep to actual, observed call sites with file:line references. Group by:
- Cross-domain `.Include()` calls
- Cross-section direct DbContext reads
- Within-section cross-service direct DbContext reads (§2c)
- Inline `IMemoryCache` usage in service methods
- Cross-domain nav properties on this section's entities
- §8 gaps (tables this section touches but §8 does not list)

Strike-through items (`~~...~~`) and inline-annotate when a PR resolves them;
delete them only once ALL violations in the section are gone and the section
flips to Status (A).
-->

- **Cross-domain `.Include()` calls:**
  - `<file>.cs:<line>` — `<snippet>` (<other section>)
- **Cross-section direct DbContext reads:**
  - `<file>.cs:<line>` — `<snippet>` (<other section>)
- **Inline `IMemoryCache` usage in service methods:**
  - `<file>.cs:<line>` — `<snippet>`
- **Cross-domain nav properties on this section's entities:**
  - `<Entity>.<Nav>` → <other section>

#### Touch-and-clean guidance

- When touching <file:line>, do X instead of Y ...
- ...

<!--
Each bullet is actionable advice a PR author can follow WITHOUT a full section
migration. The goal: every PR that touches the section ends with slightly less
debt than it started. Keep these grounded in file:line references.
-->

### For (C) Pre-migration sections

> **Status (pre-migration):** This section currently follows the "services in Infrastructure, direct DbContext" model. It will be migrated to the §15 repository pattern per [`../architecture/design-rules.md`](../architecture/design-rules.md). **Delete this block once the migration lands and this section's services live in `Humans.Application` with `*Repository.cs` impls in `Humans.Infrastructure/Repositories/`.**

Use the same three sub-blocks as (B): `#### Target repositories`, `#### Current violations`, `#### Touch-and-clean guidance`. Delete the (A) and (B) blocks.
```

---

## Authoring rules

Rules that apply when writing or updating any section doc:

1. **Invariants are present tense, not aspirational.** "A department can have at most one management role" — not "a department should have..." or "we plan to enforce...". If it's aspirational, it belongs under Architecture, not Invariants.
2. **Ownership is always stated twice.** `design-rules.md §8` is the authoritative table; the Architecture footer in this doc mirrors it. They drift in practice — do not let that drift reach a PR. If you add a new table to a section, edit both files in the same commit.
3. **Cross-section dependencies name the interface.** "Calls `IProfileService.GetByUserIdsAsync`" — not "reads profiles" or "integrates with Profiles." The interface name is the contract; the word "integrates" is a code smell.
4. **Architecture tests are cited inline.** When §15 or §12 or §6 is enforced by a test, name the file. Readers should be able to jump from the invariant to the guardrail without grep.
5. **"Current violations" is the ONLY place to list violations.** Do not let them leak into Invariants (those are what IS true, not what IS broken). Status (A) sections have no "Current violations" block — full stop.
6. **Terse wins.** Every bullet that could be shortened without losing meaning should be. Readers will stop reading a bloated doc; they will not stop reading at the point the bullet becomes terse.
7. **No emojis, no narrative ("let's"), no commentary ("interestingly..."), no "as of today"** — anchor every date with a concrete reference ("2026-04-22", "PR #243").
8. **Link to specs, do not duplicate.** Workflows belong in `docs/features/<feature>.md`; UI text choices belong in CLAUDE.md; entity field listings belong in `docs/architecture/data-model.md`. Section docs point to those files, they do not copy from them.

---

## What sections do NOT contain

Explicit out-of-scope list, to keep docs from drifting into other files' territory:

- **No workflow diagrams.** State machines live under `docs/features/<feature>.md`. The section doc states "Status follows: Submitted → Approved/Rejected" and stops.
- **No implementation walk-throughs.** If a PR needs one, it goes in the PR description, not the section doc.
- **No open questions, TODOs, "thinking about..." notes.** Those are issues on GitHub or lines in `todos.md`.
- **No deploy / infra notes.** CLAUDE.md owns deployment; section docs describe the section as it runs, not how it ships.
- **No "before" state.** Section docs describe the current and target architectures only. The history is in git.

Note: **entity field tables DO belong here** (under `## Data Model`). The central `docs/architecture/data-model.md` is an index + cross-cutting rule sheet only. A section that owns a table owns the field list for that table.

---

## Adding a new section

When a new major section is introduced:

1. **Add it to `docs/architecture/design-rules.md §8`** (Table Ownership Map) in the same PR that introduces it. Include owning services and owned tables.
2. **Copy the canonical shape** above into `docs/sections/<SectionName>.md` and fill it in.
3. **Pick a migration status.** New sections **must** be (A) from day one per `design-rules.md §15h(1)` — new code goes straight into `Humans.Application` with a repository. Starting in (B) or (C) is a design-rules violation, not a shortcut.
4. **Add a link in the project CLAUDE.md's "Extended Docs" table** if the section is significant enough to flag at the project level.
5. **If the section owns user-scoped tables**, implement `IUserDataContributor` per `design-rules.md §8a`. The architecture tests (`GdprExportDependencyInjectionTests`) will fail until it is wired up.
6. **If the section needs resource-based authorization**, add a `<Section>AuthorizationHandler` + `<Section>OperationRequirement` pair per `design-rules.md §11`. Do not invent a new auth pattern.
7. **Every new page gets a nav link.** CLAUDE.md: "No orphan pages." This applies the moment the section introduces its first controller.
