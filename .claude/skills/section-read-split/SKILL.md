---
name: section-read-split
description: "Introduce the cross-section read interface boundary (I<Section>ServiceRead) for one section's service per memory/architecture/section-read-write-split.md. Audits the surface, evaluates which methods belong on the read interface, sets up the workspace, dispatches a subagent that introduces the interface and migrates non-section callers, opens a PR. Use when the user says 'read split for X', 'split <Section>Service', 'add I<Section>ServiceRead boundary', 'apply the section-read-write-split rule to <Section>', or any variation of carving the cross-section read surface out of a section's full service interface. Reference implementation is Teams (PR 678). Operates on one section per invocation."
argument-hint: "Users | Camps | Calendar | Consent | Legal | Tickets | <SectionName>"
---

# Section Read/Write Split

## Purpose

Apply the read/write interface split — defined in [`memory/architecture/section-read-write-split.md`](../../../memory/architecture/section-read-write-split.md) — to one section's service. External sections that only read end up depending on the narrow `I<Section>ServiceRead`; writes, cache hooks, and section-internal reads stay on the full `I<Section>Service : I<Section>ServiceRead`. Active mode: skill sets up the workspace, dispatches a subagent that implements and opens the PR, reports the URL.

## Vision

Every cross-section-consumed service has a narrow read interface that returns only its own projections — `*Info`, `*Dto` or whatever that section already uses. External sections never see EF entities of another section, never see write methods that aren't theirs, never accidentally invalidate caches they don't own. Today the boundary is advisory; a future Roslyn analyzer enforces. Each invocation of this skill closes the gap for one more section.

**Teams is the reference.** PR [#678](https://github.com/peterdrier/Humans/pull/678) introduced `ITeamServiceRead` with 4 methods, migrated 23 production files, and folded in 3 audit-driven surface reductions. The skill operationalizes that pattern.

## Input

- `<SectionName>` — section to split (e.g., `Users`, `Camps`, `Calendar`). Names the section only. Its service interface, that interface's project, and its projection types are all **discovered** in Phase 0, never derived from the section name — see 0.0.
- `<empty>` — ask which section.

## Phase 0 — Pre-flight (in-session, before worktree)

Run sequentially. If any check fails or surfaces ambiguity, stop and ask the user. Don't proceed to worktree creation until Phase 0 is clean.

**Notation:** `I<Section>Service`, `<Section>Info` and `I<Section>ServiceRead` below are placeholders for the types Phase 0 resolves — not literal names to construct from `<SectionName>`. For Expenses they read `IExpenseReportService`, `ExpenseReportDto` and `IExpenseReportServiceRead`: the service name drops the plural, and the projection carries neither the section name nor the `Info` suffix. Substitute the resolved names throughout.

### 0.0 — Resolve the section's roots

**Nothing in this skill may assert a path or a type name. Discover both, once, here.** A section's layout varies along one axis that no naming convention predicts: whether its contracts sit in a sibling `.Contracts` project or an in-project `Contracts/` folder. Its service and projection type names vary too. Establish `$ROOTS` first; every step below searches it.

```bash
SECTION=<SectionName>
ROOTS=$(ls -d src/Sections/Humans.$SECTION \
              src/Sections/Humans.$SECTION.Contracts 2>/dev/null)
[ -n "$ROOTS" ] || { echo "no such section"; exit 1; }

# Sections don't all have the same folders, and `git grep pat a/ b/` (no `--`)
# aborts entirely if any path is missing. Filter every multi-path list.
exist() { for p in "$@"; do [ -e "$p" ] && printf '%s ' "$p"; done; }
```

A section without a `.Contracts` sibling keeps its contracts in an in-project `Contracts/` folder instead — `$ROOTS` still yields just the one project root; Phase B.2 resolves which shape applies.

Resolve the doc and test homes here too, and **carry these variables into the later phases** — Phase B.6 and Phase D consume them:

```bash
DOC=src/Sections/Humans.$SECTION/Docs/$SECTION.md
TESTS=tests/Humans.$SECTION.Tests
```

For Events that is `src/Sections/Humans.Events/Docs/Events.md` and `tests/Humans.Events.Tests` — there is no `docs/sections/Events.md`. The same is true of Teams and every other section.

### 0.1 — Section is real and cross-section-consumed

**The service type name is not the section name.** `Expenses` exposes `IExpenseReportService`, `Events` exposes `IEventService`, `Tickets` exposes `ITicketService` — a synthesized `I<Section>Service` misses all three. Find the declaration rather than a filename; the type is often declared inside another file, so a `find -name` cannot see it either:

```bash
grep -rnE '^[[:space:]]*(public|internal) interface I[A-Za-z]+Service[[:space:]]*[:{]' \
     $ROOTS --include='*.cs' | grep -v '/obj/'
```

Both modifiers matter: a section-internal interface is `internal`, while one promoted to a cross-section contract is `public` and lives in the section's contracts (`IHoldedService` is `public` in `Humans.Holded.Contracts`; only `IHoldedAdminService` is `internal` under `Services/`).

- **One match** — that's the section's service. Note the file; Phase B.3 edits it.
- **Several** — normal for larger sections (Tickets has five, Teams three). List them for the user with their modifiers and ask which to split. Do not guess.
- **None** — the section has no service interface; tell the user and stop.

Then count external callers of the resolved name:

```bash
reforge callers <IResolvedService> --format json
```

Filter out callers **inside `$OWNED`**, resolved below. `$ROOTS` already covers the section's own controllers, views, models, services and repository — all in-project — so `$OWNED` only needs to add anything a section keeps outside its own tree under a non-obvious name (e.g. an authorization handler filed under a different name than the section).

```bash
OWNED="$ROOTS"
# Add any of the section's own files that live outside $ROOTS under a
# non-obvious name (e.g. an authorization handler), if found.
```

If **zero external callers remain after excluding `$OWNED`**, the read interface buys nothing — tell the user and stop.

### 0.1b — Does a read interface already exist?

**Check before creating one.** A section may already have an `I<Service>ServiceRead`, and it is not always in contracts: `ICalendarServiceRead` is declared `internal` inside `src/Sections/Humans.Calendar/Services/ICalendarService.cs`, with `ICalendarService : ICalendarServiceRead` already in place. Because it is declared in the same file as the full interface, a filename search never finds it — search declarations, the same way 0.1 and 0.2 do:

```bash
grep -rnE '^[[:space:]]*(public|internal) interface I[A-Za-z]+ServiceRead\b' \
     $ROOTS --include='*.cs' | grep -v '/obj/'
```

- **Found, and the full interface already inherits it** — the split exists. Report what it covers and ask the user whether the goal is to *promote* it (make it `public`, move it to the section's contracts, re-point external callers) or to widen its method set. **Do not run B.2** — creating a second `I<Section>ServiceRead` in the contracts namespace leaves the existing same-namespace internal type winning name resolution inside the section, so the full interface keeps inheriting the old one, DI keeps registering the old one, and the new public contract is inert.
- **Found but not inherited** — that is the real defect; fix the inheritance rather than adding a type.
- **Not found** — proceed to 0.2.

### 0.2 — Section has a projection type

The architectural rule requires the read interface to return projections, never entities.

Neither the location nor the name of a projection is predictable. There is no `Services/Models/` convention in this repo — `EventInfo` is under `Humans.Events/Services/Dtos/`, `LegalDocumentInfo` sits directly in `Humans.Consent/Services/`, `TicketStubInfo` in `Humans.Tickets.Contracts/`, and `TeamInfo` is not a file at all: it is declared inside `Humans.Teams.Contracts/ITeamService.cs`. **The `*Info` suffix is not the convention either** — across `src/Sections` the projection records run `Dto` (52), `Result` (51), `Row` (41), `Snapshot` (35), `Model` (33), `Info` (26), `Summary` (18). Expenses has no `*Info` type at all; its read surface returns `ExpenseReportDto`. Filtering on a suffix is what produces the false "section has no projection" stop.

So search for the *shape* — a record declared outside the entity folder — and read the results:

```bash
grep -rnE '^[[:space:]]*(public|internal)([[:space:]]+sealed)? record([[:space:]]+struct)?[[:space:]]+[A-Za-z]+' \
     $ROOTS --include='*.cs' | grep -v '/obj/' | grep -v '/Domain/'
```

Records are the projection idiom here; entities are classes under `Domain/`, which the last filter drops. Searching declarations rather than filenames is also what finds `TeamInfo`, and it avoids matching every section's `Properties/AssemblyInfo.cs`.

Confirm at least one result is actually returned by the service's read methods — a record that exists but is only used as a request/command payload (`AdminLegalDocumentUpsertRequest`) is not a projection. If the section genuinely has no projection type:

- **Stop.** Tell the user the section needs a projection PR first (extract the projection from the entity, populate via service, cache if applicable). Reference Teams' `TeamInfo` as the shape template. Do not attempt to invent the projection inside this PR — it's a separate concern with its own callsite migration.

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

## Phase 1 — Workspace + subagent dispatch

Branch off origin/main (per `memory/process/worktrees-off-origin-main.md`), in a worktree locally and in the repo root in a cloud run (per `memory/process/always-use-worktree.md`):

```
git fetch origin --quiet
if [ "$CLAUDE_CODE_REMOTE" = "true" ]; then  # ephemeral single-session container — no worktree
  git checkout -b feat/<lower-section>-service-read-split origin/main
else
  git worktree add .worktrees/section-read-split-<lower-section> -b feat/<lower-section>-service-read-split origin/main
fi
```

Dispatch a single subagent. Locally, use `isolation: "worktree"` pointing at the worktree path; in a cloud run drop the flag — the subagent works in the repo root on the branch just created. The subagent prompt embeds the approved plan from Phase 0 — the read surface, skip cases, naming renames, Tier 1A/1B verifications. The skill **does not parallelize phases** inside the subagent (each phase depends on the previous: interface must exist before callers can swap).

Subagent model: Sonnet (mechanical refactor work; complexity is in the surface design which the main session already settled).

## Subagent execution plan (the prompt template)

The subagent receives this plan, with `<Section>` / `<section>` / method names substituted from the Phase 0 proposal.

### Pre-flight

- Working directory is the workspace root — the worktree locally, the repo root in a cloud run. Branch `feat/<lower-section>-service-read-split` is checked out off `origin/main`.
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

Confirm 0.1b found no existing read interface before creating one.

A read interface that external sections consume is a cross-section contract, so it goes with the section's other contracts. That is where the ones serving external callers live — `ITeamServiceRead`, `IEventServiceRead`, `ITicketServiceRead` and the rest. It is **not** a universal rule, though: `ICalendarServiceRead` is `internal`, declared inside `Humans.Calendar/Services/ICalendarService.cs`, because it draws the read boundary *within* the section rather than for outside consumers. Placement follows who consumes it. Match whichever contracts shape the section already uses:

| Section shape | New file | Namespace |
|---|---|---|
| Sibling contracts project (`Humans.Teams.Contracts`, most sections) | `src/Sections/Humans.<Section>.Contracts/I<Section>ServiceRead.cs` | `Humans.<Section>.Contracts` |
| In-project contracts folder (`Humans.Store`, `Humans.Feedback`) | `src/Sections/Humans.<Section>/Contracts/I<Section>ServiceRead.cs` | `Humans.<Section>.Contracts` |

Both shapes use the same namespace, so only the file path differs. If the section has neither a sibling `.Contracts` project nor a `Contracts/` folder, create the folder in-project — that's the lighter of the two and needs no new `.csproj` or reference edits.

```csharp
namespace Humans.<Section>.Contracts;

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

`I<Section>Service` may live in a different project than the read interface — the full service interface is often under `Services/` while the read interface is in contracts. Make sure the project owning `I<Section>Service` references the contracts project before B.3.

#### B.3 — Modify `I<Section>Service.cs`

- Change declaration to `public interface I<Section>Service : I<Section>ServiceRead`.
- Remove signatures now inherited from the read interface (don't duplicate — duplicates cause CS0108 hide warnings).
- Keep writes, cache hooks, Teams-internal reads, entity-returners, and the renamed `Get<Section>EntityBy<Key>Async`.

#### B.4 — Update impl + caching decorator

- Add new projection-returning methods (e.g. `GetBySlugAsync(slug) → <Section>Info?`). On the caching decorator, these are typically a one-line `Values.FirstOrDefault(x => x.Slug == slug)` against `TrackedCache.Values` — no repo hit on warm cache.
- On the inner service, delegate or compute from cached state where applicable.

#### B.5 — DI registration

In the section's DI registration — `src/Sections/Humans.<Section>/Section.cs` (`ISection.Register`):

```csharp
services.AddSingleton<Caching<Section>Service>();
services.AddSingleton<I<Section>Service>(sp => sp.GetRequiredService<Caching<Section>Service>());
services.AddSingleton<I<Section>ServiceRead>(sp => sp.GetRequiredService<Caching<Section>Service>());
services.AddHostedService(sp => sp.GetRequiredService<Caching<Section>Service>());
```

Both interfaces resolve to the same singleton.

#### B.6 — Architecture tests

In the section's architecture test file under `$TESTS` — `$TESTS/<Section>ArchitectureTests.cs` at the project root (e.g. `tests/Humans.Events.Tests/EventsArchitectureTests.cs`) — or a new file there if missing, assert:

**Do not put a section's test in any other test project.** No other project references the section assembly, and a section grants `InternalsVisibleTo` only to `Humans.<Section>.Tests` and `Humans.Integration.Tests`. Since section service interfaces are `internal`, a test placed elsewhere cannot see the type under test and will not compile — and even if it did, it could not build the section's production DI registration to check the resolution below.

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

For each file outside the section's own tree — exclude everything under `$ROOTS` as resolved in 0.0, the same scope 0.1 used for the caller count, plus the section's auth handler if it's filed under a different name:

1. Read the file's actual `I<Section>Service` usages.
2. If **every call** is to a method on `I<Section>ServiceRead`, swap the field/ctor parameter type from `I<Section>Service` → `I<Section>ServiceRead`. Update field name if it follows a `_section`/`section` convention.
3. If **any call** is to a write, a cache hook, the renamed entity-returning method, a deferred user-projection method, or any other method on the full interface, **skip the file silently**. Don't add TODO comments. Don't open per-caller issues. Don't expand the projection to enable migration.

#### C.2 — Sweep architecture tests across the repo

Many sections have architecture tests that reference `I<Section>Service` for dependency checks. These are spread across per-section test projects, so grep all of `tests/`:
```
grep -rnE 'I<Section>Service\b' --include='*.cs' tests/
```

For each match, evaluate: is the test asserting "this section depends on `I<Section>Service`" (write-bearing dependency, keep) or "this section reads from `I<Section>Service`" (read-only, swap to `I<Section>ServiceRead`)?

#### C.3 — Baseline files

If the repo has `tests/Humans.Web.Tests/Architecture/Baselines/*.txt` files (these are repo-wide, not per-section) that enumerate methods or interface names that changed (renames, deletions, additions), update them. Common pattern: a baseline lists "entity-returning reads in Application services" — the rename in B.1 may update it.

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

1. `$DOC` — the invariant doc resolved in 0.0 at `src/Sections/Humans.<Section>/Docs/<Section>.md`, NOT `docs/sections/`. Add under "Architecture":
   ```markdown
   - **Read/write interface split.** `I<Section>ServiceRead` (N methods: ...) is the cross-section read surface — only `<Section>Info` projections, no EF entities. `I<Section>Service : I<Section>ServiceRead` adds writes, cache invalidation, and <Section>-internal reads. External sections inject `I<Section>ServiceRead`. See `memory/architecture/section-read-write-split.md`.
   ```

2. If memory atom is missing, recreate from PR 678's content. If section-template addendum is missing, recreate from PR 678's content. **Both should already exist** — this is a defensive fallback only.

3. `docs/architecture/maintenance-log.md` — update this section's **Section Refactor History** row: Last Lane (date + PR) and Post-Lane Score (the section's built `reforge surface-score` after the final commit).

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
- [`src/Sections/Humans.Teams/Docs/Teams.md`](../../../src/Sections/Humans.Teams/Docs/Teams.md) — reference implementation. Its doc lives in the project; there is no `docs/sections/Teams.md`.
- Teams PR [#678](https://github.com/peterdrier/Humans/pull/678) — first application, 23 files migrated, 3 audit cleanups, 1 audit deviation.
- [`.claude/skills/audit-surface/`](../audit-surface/) — invoked in Phase 0.4 (lives in `~/.claude/skills/audit-surface/`, not this repo).
- [`.claude/skills/reforge/SKILL.md`](../reforge/SKILL.md) — invoked in Phase 0.1 for caller enumeration.
- [`.claude/skills/section-align/SKILL.md`](../section-align/SKILL.md) — broader sibling that also touches cross-section boundaries; this skill is narrower.
