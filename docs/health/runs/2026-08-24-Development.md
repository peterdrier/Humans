# doctor(Development) — 2026-08-24

## Header

- Invocation: unattended daily run, no arguments (routine-fired)
- Anchor commit: `origin/main` @ `28f62ad8`
- Budget: 2.5h
- Branch: `section-doctor/2026-08-24T071255Z`
- PR: pending
- 2026-08-24 07:12Z session: dotnet SDK 10.0.400, reforge and dotnet-ef available.
  Stryker not installed — Tests-thread mutation-score half skipped with reason.

## Assessment summary

Small section (score=273, loc=1568), never doctored, healthy shape: two
controllers over three internal seeders, no owned tables, no resource set.
`Section.Register` fails closed and the Admin persona gate is triple-locked
(controller check, chooser check, `PersonasFor` predicate) so the panel and
route agree — all deliberate belt-and-braces because the failure mode is
anonymous Admin over real QA data.

The material finding is doc drift, not shape drift: `Contracts/README.md`
still lists service names that G5 collapse renamed (`ICampService`,
`ICampRoleService`, `IShiftManagementService`, `IShiftSignupService`), and
`Development.md` names two pinning tests that never shipped
(`SectionTakesNoDbContextOrRepository`, the `IStringLocalizer<T>` guard). The
`Development.md` freshness-triggers block is otherwise accurate.

Two small code items: three mojibake em-dashes in `DevPersonaSeeder.cs` doc
comments (finding #3), and an identical `PascalToKebab` helper duplicated
between `DevLoginController` and `DevPersonaSeeder` (finding #4).

## Ranked findings

1. `Contracts/README.md` names dependencies that no longer exist — service
   names collapsed at each dependency's G5 (`ICampService` →
   `ICampServiceRead`/`ICampSeeding`; `ICampRoleService` → `ICampRoleSeeding`;
   `IShiftManagementService` → `IShiftSeeding` + `IBurnSettingsService`;
   `IShiftSignupService` → `IShiftSignupSeeding`). Doc lists 12; real is 18,
   and 4 of the 12 are wrong strings. — **dedup w/ Development.md** (docs)
2. `Development.md` claims `DevelopmentArchitectureTests.SectionTakesNoDbContextOrRepository`
   pins the "no DbContext, no repository" invariant, and
   `DevelopmentArchitectureTests` pins the "no `IStringLocalizer<T>`"
   invariant. Neither test exists in `tests/Humans.Development.Tests/DevelopmentArchitectureTests.cs`
   (only `Register_binds_nothing_…` is there). Doc → truthful; missing tests
   move to Needs Peter. — **freshness** (docs)
3. `DevPersonaSeeder.cs` doc comments carry three UTF-8 mojibake em-dashes
   (`â€”` at lines 73, 316, 687) from a historical encoding round-trip —
   pure comment cleanup. — **cut** (comments)
4. `DevLoginController.PascalToKebab` (10 lines) is byte-identical to
   `DevPersonaSeeder.PascalToKebab` (10 lines). Same assembly, same
   behaviour. — **dedup** (code)
5. `Development.md` Cross-Section Dependencies table lists `IHumanLifecycleService`
   under a separate "Human Lifecycle" heading — it lives in Users. Minor
   misrouting of the row heading; keep the entry, note in Users' row.
   Deferred to the next run — not the shape of this run. — **freshness**
   (deferred, non-blocking)
6. Three `HUM_USER_DISPLAYNAME` analyzer warnings in section code
   (`DevPersonaSeeder.cs:104,324`, `DevelopmentDashboardSeeder.cs:258`) at
   `new User { DisplayName = display, BurnerName = display }` — creation-time
   BurnerName fallback, which the analyzer message names as a legitimate
   consumer, but the warning still fires. Cross-section decision (whether
   creation-time uses should be suppressed section-wide or the analyzer's
   allowlist should recognise the pattern). — **Needs Peter #2**

Independence check: pass — #1/#2/#5 came from cross-checking the target's
"stated behavior" against the code and test tree, #3 from reading the source,
#4 from Shape lens (two files with byte-identical helper). Not a scanner
verdict list.

## File coverage

Every path in the 3a inventory has a disposition here:

| Path | Disposition | Thread |
|---|---|---|
| `src/Sections/Humans.Development/Contracts/README.md` | changed | Freshness (Strike 2) |
| `src/Sections/Humans.Development/Controllers/DevLoginController.cs` | changed | Shape (Strike 4) |
| `src/Sections/Humans.Development/Controllers/DevSeedController.cs` | reviewed | Behavior & bugs |
| `src/Sections/Humans.Development/Docs/Development.md` | changed | Freshness (Strike 3) |
| `src/Sections/Humans.Development/Docs/authorization.md` | reviewed | Freshness |
| `src/Sections/Humans.Development/Docs/health.md` | changed | Phase 3c (new) |
| `src/Sections/Humans.Development/Humans.Development.csproj` | reviewed | Freshness |
| `src/Sections/Humans.Development/Section.cs` | reviewed | Shape |
| `src/Sections/Humans.Development/SectionAdminNav.cs` | reviewed | Shape |
| `src/Sections/Humans.Development/Services/AuditEntityTypes.cs` | reviewed | Comments |
| `src/Sections/Humans.Development/Services/DevPersonaSeeder.cs` | changed | Shape / Comments (Strikes 1, 4) |
| `src/Sections/Humans.Development/Services/DevelopmentCampRoleSeeder.cs` | reviewed | Shape |
| `src/Sections/Humans.Development/Services/DevelopmentDashboardSeeder.cs` | reviewed | Shape |
| `src/Sections/Humans.Development/Views/DevLogin/Users.cshtml` | reviewed | Prose & surface |
| `src/Sections/Humans.Development/Views/Shared/_DevLoginPanel.cshtml` | reviewed | Prose & surface |
| `src/Sections/Humans.Development/Views/_ViewImports.cshtml` | reviewed | Prose & surface |
| `tests/Humans.Development.Tests/DevLoginControllerTests.cs` | reviewed | Tests |
| `tests/Humans.Development.Tests/DevPersonaSeederTests.cs` | reviewed | Tests |
| `tests/Humans.Development.Tests/DevSeedControllerTests.cs` | reviewed | Tests |
| `tests/Humans.Development.Tests/DevelopmentArchitectureTests.cs` | reviewed | Tests |
| `tests/Humans.Development.Tests/Humans.Development.Tests.csproj` | reviewed | Freshness |

## Threads

| Thread | How | Model | Findings | Notes |
|---|---|---|---|---|
| Spine + Shape + Behavior & bugs (3a–3c, 3e) | main | opus-5 | #1,#2,#3,#4,#6 | shared cost (Phase 3 marked once) |
| Freshness | main (self-run) | opus-5 | #1,#2,#5 | dispatched-thread degrade path: sub-agent skipped in unattended run to keep the strike loop moving |
| Conformance | inspected in-band | — | none | `section-conformance.yml` rows (file-layout, resource-key-prefix, table-prefix) all pass — no resx, no tables, canonical layer folders |
| Tests | main (self-run) | opus-5 | (#2, #6) | Stryker skipped — not installed in this environment; invariant matrix walked in-band — the two missing pinning tests surfaced as #2 |
| Prose & surface | main (self-run) | opus-5 | none | 3 view files reviewed in-band; no dead resx (no resx); nav discoverability fine (both admin-nav items point at existing actions) |
| History | main (self-run) | opus-5 | none | `Development.md` history block ("audit's G1 gap #1", "gap #4 known deviation") passes the cut test — every live constraint |
| Comments | main (self-run) | opus-5 | #3 | Comment-cleanup mojibake only; rest survive the cut test |
| Inbox | not run | — | — | Environment lacks `gh` CLI; the GitHub MCP tools listed 0 open PRs but no per-section open-issue call was made — deferred to next run |

Costs land in the `## Cost` block at Phase 7.

## Worked

- **Strike 1 (cut, comments):** Fix three mojibake em-dashes in
  `DevPersonaSeeder.cs` doc comments (lines 73, 316, 687).
- **Strike 2 (freshness):** `Contracts/README.md` — replace the stale service
  list with today's actual consumed surface.
- **Strike 3 (freshness):** `Development.md` — remove the two test names that
  don't ship; state the truthful pin (`Register_binds_nothing_…`); note the
  missing pinning tests are queued as Needs Peter #1.
- **Strike 4 (dedup):** Promote `DevPersonaSeeder.PascalToKebab` to
  `internal static` and delete `DevLoginController.PascalToKebab`.

## Skipped

- **Finding #5** (Users vs Human Lifecycle row heading in Cross-Section
  Dependencies) — non-blocking cosmetic; deferred to the next run so it lands
  with any other Development.md revision rather than churning the doc twice
  in a week.
- **Finding #6** (`HUM_USER_DISPLAYNAME` at creation-time sites) — Needs
  Peter #2. Suppressing three warnings in section code is a policy call, and
  the analyzer already treats these as a "legitimate consumer" per its
  message text; changing the analyzer message vs. suppressing at the call
  site is not this section's judgment.
- **Sections passed over as blocked:** none (no open `section-doctor/…` PRs
  at branch point).

## Retro

- **Selector rubric:** picked Development as the never-doctored tier median
  by reforge (score=273 of 37 pool members). Reasonable — Development is
  small, healthy, and getting its target derived cheaply feeds later runs
  that reach for a helper it exposes.
- **Wasted motion:** none material. The freshness lane's stale-service
  finding sat next to the doc-vs-tests finding, which is exactly the "read
  the doc end-to-end once the section doc is open" rule paying off.
- **Assessment missed:** nothing revealed only in striking. The mojibake was
  visible on first read of `DevPersonaSeeder.cs` (cheap to catch, cheap to
  fix) — no reason a first pass wouldn't have found it.
- **Target diff:** the section had no prior `health.md`, so today's is the
  first snapshot. Nothing to diff. Recorded for future runs.

## Needs Peter

- [ ] 1 — Add pinning tests for the two invariants `Development.md` claims
  `DevelopmentArchitectureTests` pins but doesn't: (a) constructors of
  `DevPersonaSeeder`, `DevelopmentCampRoleSeeder`, `DevelopmentDashboardSeeder`
  and both controllers take no `DbContext` and no `IRepository`; (b) no type
  in `Humans.Development` binds `IStringLocalizer<T>` for any `T`. Steward
  today: reflection over the loaded assembly, mirroring Gate's
  architecture-tests shape. Doc has been corrected in the meantime.
- [ ] 2 — Decide the section-wide policy on `HUM_USER_DISPLAYNAME` at
  creation-time sites (`DevPersonaSeeder.cs:104,324`,
  `DevelopmentDashboardSeeder.cs:258`). Options: (a) leave as-is — these are
  named-legitimate consumers in the analyzer message and warnings are
  survivable; (b) add per-call `#pragma warning disable HUM_USER_DISPLAYNAME`
  with a comment naming the "creation-time fallback" branch; (c) widen the
  analyzer's allowlist to recognise `new User { … DisplayName = X, BurnerName
  = X }` as the same value. Cross-cutting — every seeder in the repo faces
  this.

## Sweep queue

- debt: Camps — three UTF-8 mojibake em-dashes (`â€”`) in
  `src/Sections/Humans.Camps/Models/CampAdmin/CampAdminPageBuilder.cs`
  lines 74, 76, 77 — same encoding round-trip that hit Development's
  seeder comments; here they're rendered fallback strings for a page.

## Cost

Filled in at PR-creation time — see the PR body's `## Cost` block for the
authoritative figure; that same table is backfilled here in the same commit.
