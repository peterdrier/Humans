# doctor(Development) — 2026-08-24

## Header

- Invocation: unattended daily run, no arguments (routine-fired)
- Anchor commit: `origin/main` @ `28f62ad8`
- Budget: 2.5h
- Branch: `section-doctor/2026-08-24T071255Z`
- PR: peterdrier/Humans#1480
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
   under a separate "Human Lifecycle" heading — it lives in
   `Humans.Users.Contracts`. Row heading misrouted. — **freshness**
   *(struck in the post-push round, folded into finding #8's table rebuild;
   the "Human Lifecycle" row is gone and the entry sits in the Users row.
   No other doc in the repo carried that row heading.)*
6. Three `HUM_USER_DISPLAYNAME` analyzer warnings in section code
   (`DevPersonaSeeder.cs:104,324`, `DevelopmentDashboardSeeder.cs:258`) at
   `new User { DisplayName = display, BurnerName = display }` — creation-time
   BurnerName fallback, which the analyzer message names as a legitimate
   consumer, but the warning still fires. Cross-section decision (whether
   creation-time uses should be suppressed section-wide or the analyzer's
   allowlist should recognise the pattern). — **Needs Peter #2**

7. **The sweep carried a wrong diagnosis.** This run's Phase 5 sweep copied
   Onboarding's "four misbound resource keys in Users" item into the new
   `src/Sections/Humans.Users/Docs/debt.yml` verbatim, without re-verifying
   it. Codex refuted it on the PR and is right: none of the four keys exists
   in any of the repo's 156 `.resx` files, and `Admin_SortBy` already reads
   through `SharedLocalizer`, so nothing is misbound — the entries are simply
   missing. The user-visible symptom (raw key name rendered in all six
   languages) is real; the cause and therefore the fix were both wrong, and
   `/debt-sweep` would have been pointed at a no-op rebind. Entry rewritten.
   — **freshness** (struck, post-push round)
8. `Contracts/README.md` names `Docs/Development.md`'s Cross-Section
   Dependencies table authoritative, but that table omitted
   `SignInManager<User>` and `IUserServiceRead` — both real constructor
   dependencies (`DevLoginController` and `DevSeedController` respectively).
   Declaring an incomplete table authoritative preserves exactly the drift
   Strike 2 was meant to remove. Raised by Codex on the PR; table rebuilt
   from the constructors so the two catalogues are now the same set.
   — **freshness** (struck, post-push round)

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

| Thread | How | Model | Findings | Cost | Notes |
|---|---|---|---|---|---|
| Spine + Shape + Behavior & bugs (3a–3c, 3e) | main | opus-5 | #1,#2,#3,#4,#6 | shared ($6.42, phase3) | Phase 3 marked once — one bucket shared with the reading threads below |
| Freshness | main (self-run) | opus-5 | #1,#2,#5 | shared | dispatched-thread degrade path: sub-agent skipped in unattended run to keep the strike loop moving |
| Conformance | inspected in-band | — | none | shared | `section-conformance.yml` rows (file-layout, resource-key-prefix, table-prefix) all pass — no resx, no tables, canonical layer folders |
| Tests | main (self-run) | opus-5 | (#2, #6) | shared | Stryker skipped — not installed in this environment; invariant matrix walked in-band — the two missing pinning tests surfaced as #2 |
| Prose & surface | main (self-run) | opus-5 | none | shared | 3 view files reviewed in-band; no dead resx (no resx); nav discoverability fine (both admin-nav items point at existing actions) |
| History | main (self-run) | opus-5 | none | shared | `Development.md` history block ("audit's G1 gap #1", "gap #4 known deviation") passes the cut test — every live constraint |
| Comments | main (self-run) | opus-5 | #3 | shared | Comment-cleanup mojibake only; rest survive the cut test |
| Inbox | not run | — | — | — | Environment lacks `gh` CLI; the GitHub MCP tools listed 0 open PRs but no per-section open-issue call was made — deferred to next run |

The `assess` row of `## Cost` is the shared bucket for every main-thread reading
lens; per the skill, splitting the turn-by-turn cost per lens would be an
invented number.

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
- **Strike 5 (post-push, findings #7 + #5 + #8):** Rewrite the swept Users
  debt entry with the verified diagnosis (missing resx entries, not
  misbindings) and rebuild `Development.md`'s Cross-Section Dependencies
  table from the constructors — `SignInManager<User>`, `IUserServiceRead`
  and `IHumanLifecycleService` into the Users row, the misrouted "Human
  Lifecycle" row removed. The two catalogues are now the same set.

## Skipped

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
- **Assessment missed:** nothing revealed only in striking, but the *sweep*
  missed something the assessment would have caught: finding #7. The sweep
  copied another run's debt diagnosis verbatim and it was wrong — Codex
  refuted it on the PR within minutes, and verifying took one grep across
  the repo's resx files. The sweep is the one phase in this skill that
  transcribes another run's conclusions rather than deriving its own, and
  this run treated that as a mechanical copy. Proposed as Needs Peter #3.
- **Assessment missed, second:** finding #8 — Strike 2 named
  `Development.md`'s dependency table authoritative without checking it was
  complete. It wasn't. A "defer to the canonical catalogue" edit is only
  valid if the run has read the catalogue it is deferring to.
- **Target diff:** the section had no prior `health.md`, so today's is the
  first snapshot. Nothing to diff. Recorded for future runs.

## Needs Peter

- [x] 1 — **Declined (Peter, 2026-08-24).** Proposed pinning tests for the two
  invariants `Development.md` claimed `DevelopmentArchitectureTests` pins but
  doesn't: (a) no `DbContext`/`IRepository` in the seeder and controller
  constructors; (b) no `IStringLocalizer<T>` binding anywhere in
  `Humans.Development`. Both rejected — a test asserting a section has no
  database tables is absurd. Doc was corrected in this run; nothing further.
- [x] 2 — **Answered (Peter, 2026-08-24): `HUM_USER_DISPLAYNAME` is being
  retired.** No section policy needed; the three warnings
  (`DevPersonaSeeder.cs:104,324`, `DevelopmentDashboardSeeder.cs:258`) go away
  with the analyzer. The run had asked whether to leave them, `#pragma` them,
  or widen the allowlist; none applies.
- [ ] 3 — **Note only (Peter, 2026-08-24): a run never edits the skill, and
  neither does this note — it stands for later review when Peter edits the
  skill himself.** Phase 5's sweep transcribes another run's `debt:` text into a
  ledger without re-deriving it, and finding #7 shows that ships a wrong
  diagnosis into `/debt-sweep`'s input. Proposed edit, governing **Phase 5
  (the sweep)**: before writing a swept `debt:` item, verify its central
  claim against the tree — one grep for the named symbol, key or path — and
  correct the text where it no longer holds, citing the re-derivation.
  Idempotence stays the only other bookkeeping.

## Sweep queue

- debt: Camps — three UTF-8 mojibake em-dashes (`â€”`) in
  `src/Sections/Humans.Camps/Models/CampAdmin/CampAdminPageBuilder.cs`
  lines 74, 76, 77 — same encoding round-trip that hit Development's
  seeder comments; here they're rendered fallback strings for a page.

## Cost

| Component | Phase | Model | Fresh in | Out | Cache write | Cache read | ~$ |
|---|---|---|---|---|---|---|---|
| worktree | phase1 | opus | 2 | 499 | 994 | 190,420 | 0.11 |
| select section | phase2 | opus | 16 | 1,384 | 3,032 | 577,897 | 0.34 |
| assess | phase3 | opus | 56 | 44,707 | 134,970 | 8,915,963 | 6.42 |
| strike: 1 mojibake em-dash cleanup | phase4 | opus | 11 | 3,854 | 6,041 | 2,206,406 | 1.24 |
| strike: 2 Contracts README service names | phase4 | opus | 5 | 2,117 | 3,557 | 1,021,604 | 0.59 |
| strike: 3 Development.md test-claim corrections | phase4 | opus | 6 | 2,275 | 3,554 | 1,243,454 | 0.70 |
| strike: 4 dedup PascalToKebab | phase4 | opus | 23 | 14,654 | 46,913 | 5,099,859 | 3.21 |
| bookkeeping | phase5 | opus | 8 | 5,139 | 11,740 | 1,906,612 | 1.16 |
| PR | phase7 | opus | 8 | 2,170 | 6,508 | 1,978,411 | 1.08 |
| **total** | | | 135 | 76,799 | 217,309 | 23,140,626 | **14.85** |

API-equivalent $, list rates; run under subscription quota. Measured
Phase 1 to PR creation; PR create/backfill and Phase 8 excluded.

Well below the 2026-08-23 Onboarding baseline
(nobodies-collective/Humans#1465: $93.30 / 746 calls / 184k median
context); the section is small and the assessment mostly one long
main-thread read, so nothing conclusive about the dispatch question.
