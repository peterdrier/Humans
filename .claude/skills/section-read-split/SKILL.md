---
name: section-read-split
description: "Introduce the cross-section read interface boundary (I<Section>ServiceRead) for one section's service per memory/architecture/section-read-write-split.md. Audits the surface, evaluates which methods belong on the read interface, creates a worktree, dispatches a subagent that introduces the interface and migrates non-section callers, opens a PR. Use when the user says 'read split for X', 'split <Section>Service', 'add I<Section>ServiceRead boundary', 'apply the section-read-write-split rule to <Section>', or any variation of carving the cross-section read surface out of a section's full service interface. Reference implementation is Teams (PR 678). Operates on one section per invocation."
argument-hint: "Users | Camps | Calendar | Consent | Legal | Tickets | <SectionName>"
---

# Section Read/Write Split

## Purpose

Apply the read/write interface split — defined in [`memory/architecture/section-read-write-split.md`](../../../memory/architecture/section-read-write-split.md) — to one section's service. External sections that only read end up depending on the narrow `I<Section>ServiceRead`; writes, cache hooks, and section-internal reads stay on the full `I<Section>Service : I<Section>ServiceRead`. Active mode: skill creates a worktree, dispatches a subagent that implements and opens the PR, reports the URL.

## Vision

Every cross-section-consumed service has a narrow read interface that returns only its own projections (`*Info` DTOs). External sections never see EF entities of another section, never see write methods that aren't theirs, never accidentally invalidate caches they don't own. Today the boundary is advisory; a future Roslyn analyzer enforces. Each invocation of this skill closes the gap for one more section.

**Teams is the reference.** PR [#678](https://github.com/peterdrier/Humans/pull/678) introduced `ITeamServiceRead` with 4 methods, migrated 23 production files, and folded in 3 audit-driven surface reductions. The skill operationalizes that pattern.

## Input

- `<SectionName>` — section to split (e.g., `Users`, `Camps`, `Calendar`). Resolves to `docs/sections/<SectionName>.md` + `src/Humans.Application/Interfaces/<SectionName>/I<SectionName>Service.cs`.
- `<empty>` — ask which section.

## Phase 0 — Pre-flight (in-session, before worktree)

Run sequentially. If any check fails or surfaces ambiguity, stop and ask the user. Don't proceed to worktree creation until Phase 0 is clean.

### 0.1 — Section is real and cross-section-consumed

```
test -f docs/sections/<Section>.md                 # invariant doc must exist
test -f src/Humans.Application/Interfaces/<Section>/I<Section>Service.cs
```

Use the `reforge` skill to count external (non-section) callers of `I<Section>Service`:
```
reforge callers I<Section>Service --format json
```

Filter out callers inside the section's own folder tree (Application/Services/<Section>, Application/Interfaces/<Section>, Infrastructure/{Services,Repositories}/<Section>, Web/Controllers/<Section>*Controller.cs, Web/ViewComponents related to the section). If **zero non-section callers remain**, the section isn't cross-section-consumed — the read interface buys nothing. Tell the user and stop.

### 0.2 — Section has an `*Info` projection

Look for `<Section>Info` (or analogous DTO name) in `src/Humans.Application/Services/<Section>/Models/` or `src/Humans.Application/Models/<Section>/`. The architectural rule requires the read interface to return projections, never entities. If the section has no `*Info` type:

- **Stop.** Tell the user the section needs a projection PR first (extract `<Section>Info` from the entity, populate via service, cache if applicable). Reference Teams' `TeamInfo` as the shape template. Do not attempt to invent the projection inside this PR — it's a separate concern with its own callsite migration.

### 0.3 — Architectural rule artifacts exist

```
test -f memory/architecture/section-read-write-split.md
grep -q "Cross-section read interface" docs/sections/SECTION-TEMPLATE.md
```

Both must exist (created in PR 678). If either is missing, surface and ask whether to recreate.

### 0.4 — Run audit-surface

Invoke the audit-surface skill on `I<Section>Service`. Capture its output. The audit gives you:
- Per-method external caller count (filter to non-section)
- Body shape (passthrough-repo, composite, complex)
- Tier 1A / 1B recommendations (delete / make private)
- Tier 3 split candidates (sub-interface clusters)

**Audit findings are starting points, not directives.** Tier 1A "delete" recommendations have shipped wrong recommendations before — PR 678 caught one (a repo-level method had two live internal callers the audit missed). Before acting on any deletion, the subagent **must** verify by reading the body, grepping for callers in the impl + decorator + repo + tests, and only deleting if zero remain.

### 0.5 — Propose the read surface

Filter audit candidates by both criteria:

- **Returns a projection**, not an EF entity. `Task<<Section>Info?>`, `Task<IReadOnlyDictionary<Guid, <Section>Info>>`, `Task<IReadOnlyList<<Section>SearchHit>>` — yes. `Task<<EntityType>?>`, `Task<IReadOnlyList<<EntityType>>>` — no.
- **Has at least one non-section caller**. Reads only consumed by the section's own controllers/services stay on the full interface.

Detect known patterns and resolve before proposing:

| Pattern | Resolution |
|---|---|
| Naming collision (e.g. `GetBySlugAsync` exists returning entity AND we want a `*Info`-returning version) | Rename the entity-returning method to `Get<Section>EntityBy<Key>Async`, keep on full interface only. The new `*Info`-returning version takes the canonical name on the read interface. |
| `GetUser<X>Async` returning `<Section>Member[]` or similar | Defer. Per-user projection (`<Section>UserInfo`) is a separate follow-up PR. Method stays on full `I<Section>Service`; do not migrate user-teams-style callers. |
| `Invalidate*Cache` / `RemoveMemberFromAll*Cache` | Stay on full interface — writes against cache state, not reads. |
| Method returns a value-type aggregate (`Task<int>` counts, `Task<bool>` predicates) called cross-section | Include on read interface — it's a read. Predicates like `IsUserCoordinatorOfTeamAsync` were borderline in Teams; rule of thumb: if a non-section caller would otherwise reimplement the logic, expose it. |

Present the proposed read surface to the user:

```
Proposed I<Section>ServiceRead (N methods):
  - Method1(...) -> ProjectionType?
  - Method2(...) -> IReadOnlyDictionary<..., ProjectionType>
  - ...

Known skip cases (stay on full I<Section>Service):
  - <method>: returns entity
  - <method>: per-user projection deferred
  - <method>: write/cache hook

Audit tier 1A/1B fold-in (if any):
  - <method>: Tier 1A (delete) — N external + N internal callers verified zero
  - <method>: Tier 1B (make private) — N internal callers in impl
```

If the user is happy, proceed. Otherwise iterate the proposal until they greenlight.

## Phase 1 — Worktree + subagent dispatch

Create the worktree from origin/main (per `memory/process/worktrees-off-origin-main.md`):

```
git fetch origin --quiet
git worktree add .worktrees/section-read-split-<lower-section> -b feat/<lower-section>-service-read-split origin/main
```

Dispatch a single subagent with `isolation: "worktree"` pointing at the worktree path. The subagent prompt embeds the approved plan from Phase 0 — the read surface, skip cases, naming renames, Tier 1A/1B verifications. The skill **does not parallelize phases** inside the subagent (each phase depends on the previous: interface must exist before callers can swap).

Subagent model: Sonnet (mechanical refactor work; complexity is in the surface design which the main session already settled).

## Subagent execution plan (the prompt template)

The subagent receives this plan, with `<Section>` / `<section>` / method names substituted from the Phase 0 proposal.

### Pre-flight

- Working directory is the worktree root. Branch `feat/<lower-section>-service-read-split` is checked out off `origin/main`.
- Build green from clean: `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet`. If not green, stop and report.
- Delete any audit JSON artifacts in the repo root (`*-surface.json`, `*-downstream.json`, `*-classified.json`) so they don't end up in the diff.

### Phase A — Audit cleanup (if Tier 1A/1B was approved in Phase 0)

For each Tier 1A "delete":
- **Verify before deleting.** Grep the impl, the caching decorator, the repository interface and impl, and tests for the method name. If any production caller exists (other than the caching decorator's pass-through), keep it and move on with a note in the PR footer under "Audit deviations." Tier 1A is allowed to be wrong; the PR description records the deviation.
- If verified zero callers, delete from interface + impl + caching decorator + repository interface + repository impl + all tests.

For each Tier 1B "make private":
- Remove from `I<Section>Service`.
- Remove from caching decorator (delegation no longer needed).
- Change visibility to `private` on the impl class.
- Internal callers still compile against the private method.
- Tests targeting the method directly should reframe against public callers or be deleted if redundant.

Build + test green between commits.

Commit: `chore(<section>): drop dead/internal interface surface per audit`

### Phase B — Introduce `I<Section>ServiceRead`

#### B.1 — Handle naming collisions

For each rename case identified in Phase 0 (entity-returning method colliding with new projection-returning name):
- Rename the existing entity-returning method to `Get<Section>EntityBy<Key>Async` on `I<Section>Service`, impl, caching decorator, and all callers.
- Build + test green.

Commit (or fold into B.2): `refactor(<section>): rename entity-returning <method> to <newName>`

#### B.2 — Create the read interface

New file: `src/Humans.Application/Interfaces/<Section>/I<Section>ServiceRead.cs`

```csharp
namespace Humans.Application.Interfaces.<Section>;

/// <summary>
/// Cross-section read surface for the <Section> section. External sections inject
/// this interface; only <Section>Info / <Section>SearchHit projections, no EF entities.
/// See memory/architecture/section-read-write-split.md.
/// </summary>
public interface I<Section>ServiceRead
{
    // Methods from Phase 0 proposal
}
```

#### B.3 — Modify `I<Section>Service.cs`

- Change declaration to `public interface I<Section>Service : I<Section>ServiceRead`.
- Remove signatures now inherited from the read interface (don't duplicate — duplicates cause CS0108 hide warnings).
- Keep writes, cache hooks, Teams-internal reads, entity-returners, and the renamed `Get<Section>EntityBy<Key>Async`.

#### B.4 — Update impl + caching decorator

- Add new projection-returning methods (e.g. `GetBySlugAsync(slug) → <Section>Info?`). On the caching decorator, these are typically a one-line `Values.FirstOrDefault(x => x.Slug == slug)` against `TrackedCache.Values` — no repo hit on warm cache.
- On the inner service, delegate or compute from cached state where applicable.

#### B.5 — DI registration

In the section's DI extension method (likely `src/Humans.Web/Extensions/Sections/<Section>SectionExtensions.cs`):

```csharp
services.AddSingleton<Caching<Section>Service>();
services.AddSingleton<I<Section>Service>(sp => sp.GetRequiredService<Caching<Section>Service>());
services.AddSingleton<I<Section>ServiceRead>(sp => sp.GetRequiredService<Caching<Section>Service>());
services.AddHostedService(sp => sp.GetRequiredService<Caching<Section>Service>());
```

Both interfaces resolve to the same singleton.

#### B.6 — Architecture tests

In `tests/Humans.Application.Tests/Architecture/<Section>ArchitectureTests.cs` (or new file if missing), assert:
- `I<Section>Service` inherits from `I<Section>ServiceRead`.
- Both interfaces DI-resolve to the same concrete instance from a service-provider built from the production DI registration.
- Add a positive smoke test for the new projection-returning method (e.g. by-slug returns same data as the entity-returning version for a known row).

Build + test green.

Commit: `feat(<section>): introduce I<Section>ServiceRead boundary`

### Phase C — Migrate non-section external callers

#### C.1 — Identify candidates

```
grep -rnE 'I<Section>Service\b' --include='*.cs' src/ tests/ | grep -v 'I<Section>ServiceRead'
```

For each file outside the section's own folder tree (exclude `src/Humans.Application/{Services,Interfaces}/<Section>/**`, `src/Humans.Infrastructure/{Services,Repositories}/<Section>/**`, `src/Humans.Web/Controllers/<Section>*Controller.cs`, the section's auth handlers, the section's ViewModels):

1. Read the file's actual `I<Section>Service` usages.
2. If **every call** is to a method on `I<Section>ServiceRead`, swap the field/ctor parameter type from `I<Section>Service` → `I<Section>ServiceRead`. Update field name if it follows a `_section`/`section` convention.
3. If **any call** is to a write, a cache hook, the renamed entity-returning method, a deferred user-projection method, or any other method on the full interface, **skip the file silently**. Don't add TODO comments. Don't open per-caller issues. Don't expand the projection to enable migration.

#### C.2 — Sweep architecture tests across the repo

Many sections have architecture tests that reference `I<Section>Service` for dependency checks. Grep:
```
grep -rnE 'I<Section>Service\b' --include='*.cs' tests/Humans.Application.Tests/Architecture/
```

For each match, evaluate: is the test asserting "this section depends on `I<Section>Service`" (write-bearing dependency, keep) or "this section reads from `I<Section>Service`" (read-only, swap to `I<Section>ServiceRead`)?

#### C.3 — Baseline files

If the repo has `tests/Humans.Application.Tests/Architecture/Baselines/*.txt` files that enumerate methods or interface names that changed (renames, deletions, additions), update them. Common pattern: a baseline lists "entity-returning reads in Application services" — the rename in B.1 may update it.

#### C.4 — Commits

Batch by directory or section, ≤10 files per commit. After each batch:
```
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
git push
```

Commit pattern: `refactor(<consumer-section>): consume I<Section>ServiceRead`

### Phase D — Docs

If this is the first section being split (artifacts didn't exist in Phase 0.3), create them. Otherwise just add the section reference.

1. `docs/sections/<Section>.md` — add under "Architecture":
   ```markdown
   - **Read/write interface split.** `I<Section>ServiceRead` (N methods: ...) is the cross-section read surface — only `<Section>Info` projections, no EF entities. `I<Section>Service : I<Section>ServiceRead` adds writes, cache invalidation, and <Section>-internal reads. External sections inject `I<Section>ServiceRead`. See `memory/architecture/section-read-write-split.md`.
   ```

2. If memory atom is missing, recreate from PR 678's content. If section-template addendum is missing, recreate from PR 678's content. **Both should already exist** — this is a defensive fallback only.

Commit: `docs(<section>): note read/write split + reference impl`

### Phase E — Open the PR

```
gh pr create --title "feat(<section>): introduce I<Section>ServiceRead boundary" --body "$(cat <<'EOF'
## Summary

Introduces `I<Section>ServiceRead` as the cross-section read boundary for the <Section> section. External sections that only read inject the narrow interface (N `<Section>Info` / `<Section>SearchHit`-returning methods); writes and cache hooks stay on `I<Section>Service : I<Section>ServiceRead`.

[If Tier 1A/1B folded in:]
Also folds in <count> audit-driven surface reductions.

Enforcement is advisory for now — a future Roslyn analyzer will enforce. See `memory/architecture/section-read-write-split.md` and Teams' PR #678 for the reference implementation.

### Phase C migration counts

- **Migrated to `I<Section>ServiceRead`:** <N> production files
  - Application services (<count>): <list>
  - Infrastructure (<count>): <list>
  - Web (<count>): <list>
- **Skipped (still inject `I<Section>Service`):** <N>+ files that call writes, cache hooks, entity-returning reads, deferred user-projection methods, or other full-interface members. A separate audit will sweep these.

### Audit deviations (if any)

[For each Tier 1A recommendation kept against the audit's advice:]
- `<method>` (Tier 1A "delete"): kept — <N> live internal callers in <files>.

## Test plan

- [x] `dotnet build Humans.slnx -v quiet`
- [x] `dotnet test Humans.slnx -v quiet`
- [x] New unit test: `<NewMethod>` returns same data as `<EntityVersion>` for a known row
- [x] New architecture tests: `I<Section>Service` inherits from `I<Section>ServiceRead`, both DI-resolve to same singleton
- [ ] Manual: load representative pages, confirm no regression

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

The PR description **footer must include both** the migration count breakdown and any audit deviations — these are the running tally the next-section-split skill (this one, run again) reads to track progress.

### Definition of done (subagent reports back when all true)

1. All phases committed and pushed to `origin/feat/<lower-section>-service-read-split`.
2. Build + tests green locally.
3. PR opened against `peterdrier/Humans:main`.
4. PR body includes migration summary (counts + per-bucket files migrated + per-bucket reason for skipped) and audit deviations.
5. No audit JSON artifacts in the diff.
6. Worktree path returned to skill driver for cleanup.

## Stop conditions (Phase 0)

- Section's invariant doc is missing.
- Section has no `*Info` projection — needs a separate projection PR first.
- Section has zero non-section callers of `I<Section>Service` — split buys nothing.
- Architectural rule artifacts missing — surface and ask whether to recreate (defensive; should exist post-PR 678).
- Proposed read surface has fewer than 2 methods — likely a sign the section is barely cross-section-consumed; ask whether the split is worth shipping.

Surface as: "I hit X. Decision needed: [A] / [B] / [C]."

## Lessons from PR 678 (Teams)

These have shaped the skill above; called out here so the subagent doesn't relearn them:

- **Audit Tier 1A recommendations can be wrong.** The Teams audit suggested deleting `ITeamRepository.GetPendingCountsByTeamIdsAsync`; it had two live internal callers the audit missed. The subagent verified, kept it, noted the deviation in the PR footer. The skill's verify-before-deleting step came from this.
- **Architecture tests in *other* sections need touching.** Teams' PR updated `Calendar`, `Campaigns`, `Feedback`, `TicketQuery`, `CityPlanning` architecture tests because they referenced `ITeamService`. The Phase C.2 sweep came from this.
- **Baseline files can drift.** `Baselines/ApplicationServiceEntityReadReturns.baseline.txt` needed an update from the slug rename. Phase C.3 came from this.
- **Big test-file collapse is normal.** Making `CanUserApproveRequestsForTeamAsync` private removed 110 lines from `TeamServiceTests.cs` (tests now flow through the public callers that exercise it). Don't be surprised by it.
- **Net-negative line count is healthy.** PR 678 was +263 / -300. A read-split PR that's net-positive is a sign of accidental scope expansion — push back.

## Constraints

- One section per invocation. If the user names multiple, ask which first.
- Skip silently for non-trivial migrations in Phase C. A separate audit pass will catch the leftover full-interface dependencies.
- Don't expand the section's projection (`*Info`) to enable a caller migration. That's a different PR.
- Don't open per-caller follow-up issues during the migration sweep.
- Don't introduce a Roslyn analyzer in this PR. Enforcement is intentionally advisory until the analyzer ships separately.
- Don't merge the PR. The skill stops at "PR opened"; reviewer + Peter merge.

## See also

- [`memory/architecture/section-read-write-split.md`](../../../memory/architecture/section-read-write-split.md) — the durable rule.
- [`docs/sections/SECTION-TEMPLATE.md`](../../../docs/sections/SECTION-TEMPLATE.md) — "Cross-section read interface" block.
- [`docs/sections/Teams.md`](../../../docs/sections/Teams.md) — reference implementation.
- Teams PR [#678](https://github.com/peterdrier/Humans/pull/678) — first application, 23 files migrated, 3 audit cleanups, 1 audit deviation.
- [`.claude/skills/audit-surface/`](../audit-surface/) — invoked in Phase 0.4 (lives in `~/.claude/skills/audit-surface/`, not this repo).
- [`.claude/skills/reforge/SKILL.md`](../reforge/SKILL.md) — invoked in Phase 0.1 for caller enumeration.
- [`.claude/skills/section-align/SKILL.md`](../section-align/SKILL.md) — broader sibling that also touches cross-section boundaries; this skill is narrower.
