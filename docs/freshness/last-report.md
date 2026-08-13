# Freshness Sweep — 2026-08-13

| | |
|---|---|
| Mode | diff |
| Previous anchor | `044b9e77a` |
| New anchor | `caec503c8` |
| Commits in window | 15 (13 non-merge) |
| Changed files in window | 1,689 |
| Worktree base | `origin/main` @ `c1a1076fb` (frozen) |
| Mechanical entries dirty | 10 of 11 |
| Editorial docs dirty | 135 of 141 (+3 recovered, see *Systematic findings*) |

The window is again dominated by **G5 section extraction** — 737 of the 1,689 changed files are under
`src/Sections/`. Section projects went from **19 to 35** (excluding `.Contracts`): Teams and Tickets
moved in #1280 along with a new `Humans.TicketTailor` adapter project, a nine-section batch landed in
#1269, and a brand-new **Tour** section shipped. Separately, **Peel 15 (#1273) deleted
`HumansDbContext` outright** — the type, its factory, its model snapshot, and 288 root migration
files — after merging Users and Profiles into a new `UsersDbContext`. Every table now belongs to
exactly one section context. Nearly all drift this sweep is downstream of one of those two changes.

---

## Systematic findings

### 1. 55 dead trigger globs across 17 docs — three guide docs had silently stopped firing

A repo-wide scan found **55 trigger globs pointing at paths that no longer exist**. A dead glob is
silent by construction: it makes a doc look *clean* rather than *unchecked*, so the doc never enters
the sweep's dirty list at all.

Three docs were effectively invisible and did **not** appear in this sweep's initial 135-doc dirty
list:

| Doc | Dead globs | Status |
|---|---|---|
| `docs/guide/TicketTransfers.md` | 9 of 9 | **fully dead** — had not been swept in months |
| `docs/guide/LegalAndConsent.md` | 15 of 16 | effectively dead |
| `docs/guide/Tickets.md` | 7 of 9 | effectively dead |

All 55 repaired and repointed at the durable `src/Sections/Humans.<X>/**` form. Independently
re-verified after the fact: **0 dead globs across all 143 trigger-bearing docs.**

The three recovered docs were then given a real content pass, which is where the sweep's single
most consequential fix came from (see *LegalAndConsent* below).

### 2. Root cause of the recurring dead-glob finding — the sweep's own verifier cannot detect it

This is the fifth consecutive sweep to report a large dead-glob batch. The cause is now located, in
`docs/scripts/freshness-checks/diff-mode.sh` (not CI-wired):

1. **No glob-resolution test exists.** Test 4 only checks that `<!--` / `-->` markers balance. A glob
   pointing at a deleted path passes every existing test.
2. **Test 5 is itself dead.** Its synthetic probe path is `src/Humans.Web/Controllers/TeamController.cs`,
   which no longer exists — the file moved to `src/Sections/Humans.Teams/`. The test now proves nothing.
3. **Test 4 does not walk `src/Sections/`.** It covers only `docs/sections`, `docs/features`,
   `docs/guide` — so all 35 sections' in-project docs are outside the check entirely.

Not fixed: that script is outside the catalog, editorial trees, and prune allowlist. **See Questions.**

### 2b. A false clean: two mechanical entries were skipped by triggers that could not see the code

Codex raised a P1 on this PR (`dependency-graph` and `service-data-access-map` still scoping to
`src/Humans.Application` after their services moved). Auditing **all eleven** mechanical entries for the
same defect found it was broader, and that this report had already been wrong about it.

`data-model-index` and `guid-reservations` were reported above as *"not dirty — no `Domain/Entities` or
`Data/Configurations` churn."* That was a **false clean**. G5 sections own their entities and EF
configurations outright — 24 `src/Sections/*/Domain/` and 22 `src/Sections/*/Data/Configurations/`
folders — and **39 section-owned entity/configuration files changed in the window**. The triggers only
pointed at `src/Humans.Domain/` and `src/Humans.Infrastructure/`, so both docs went unexamined while
looking clean. This is the same failure as a dead editorial glob, one layer up: *a trigger that cannot
see the code makes the doc look fresh.*

| Entry | Files matched, before → after |
|---|---|
| `data-model-index` | **0 → 39** |
| `guid-reservations` | **0 → 14** |
| `dependency-graph` | 61 → 258 (the P1) |
| `service-data-access-map` | 64 → 256 (the P1) |
| `docs-readme-index` | 62 → 95 (found by the audit; README indexes in-project section docs) |
| `controller-architecture-audit` | 13 → 42 (fixed earlier in this PR) |

All eleven entries now fire on this window. Both falsely-clean docs were re-run in this PR rather than
left for the next sweep, and **the false clean turned out to be hiding a real bug**:

- **`data-model-index`** — the Finance row conflated Finance's own entities with the separate Holded
  section's. It named `HoldedSyncState` for what is actually Finance's `HoldedDocSyncState`, and filed
  `HoldedLedgerLine` under `FinanceDbContext` when it belongs to `HoldedDbContext`. Split into correct
  Finance and Holded rows; `HoldedAccount` and `HoldedApiCall` were missing from the index entirely
  (net +3 entities). Everything else matched code — including `GoogleResource` staying under Teams
  despite living in `GoogleIntegrationDbContext`, which is intentional and cross-referenced.
- **`guid-reservations`** — genuinely clean on inspection: all 6 GUID blocks accounted for, and the two
  whose owners moved (Teams `0001`, Events `0026`) already point at their section paths.

Prompts must be fixed alongside triggers: a trigger that fires into a prompt still walking the old tree
produces a doc that looks *freshly audited* while its new rows go stale — worse than not firing at all.

**That lesson was written down here and then not applied.** Codex came back with three more P2s on the
same defect, and a systematic re-audit found a fourth: `docs-readme-index`, `guid-reservations`,
`authorization-inventory`, and `data-model-index` had widened triggers but untouched prompts. All four
are now aligned, verified by a script that compares each entry's trigger scope against its prompt scope
rather than by reading them.

A fifth review round found five more, three of them consequences of the fixes above:

- **The verifier could not report a dead catalog entry — it died before trying.** If an
  individually-listed entry like `docs/seed-data.md` were renamed, `editorial_docs()` returned a
  non-zero status from its final `if`, and under `set -euo pipefail` that aborted the whole script
  **after test 2**, so tests 3-7 never ran. The exit code was non-zero either way, but the diagnostic
  was silent. Both helper loops are now `set -e`-safe, and test 3 fails with the offending entry named.
  Proven by deliberately renaming a catalog entry and watching it fail, then restoring.
- **`dependency-graph`'s step 2** still derived a dependency's owning section from its folder under
  `src/Humans.Application/Services/` — where a moved implementation has no folder at all. Widening the
  walk without widening the classification rule would have misclassified or dropped the very G5
  dependencies the walk was added for.
- **Four EF configurations live directly under `Data/`, not `Data/Configurations/`** (Consent's three,
  Email's `EmailOutboxMessage`). The `Data/Configurations/**` globs added a round earlier still left
  `data-model-index` and `guid-reservations` falsely clean for those four files, all of which changed
  in this window. Both now watch the whole `Data/` tree.
- **`google-integration.md`** documents the `/Teams/{slug}/Resources` route table — added by this very
  sweep — but its marker never watched the Teams controller or views that serve it.
- **`docs/guide/Tickets.md`** describes vendor-adapter behaviour (incremental sync, void-to-hold
  reissue) while watching none of `Humans.TicketTailor` or the `ITicketVendorService` port.

While adding the Teams triggers I nearly shipped a dead glob of my own — `TeamResourceService.cs` is in
`src/Humans.Application/Services/GoogleIntegration/`, not the Teams project — caught by checking each
path before committing rather than after.

The `data-model-index` case is the one worth remembering. Its authority chain is **catalog → prompt-file
→ inline `freshness:auto` marker**, and the prompt-file explicitly defers to the marker. The earlier fix
edited the prompt-file, which is *dead code* during normal regeneration — the inline marker in
`data-model.md` is what actually runs, and it still walked only `src/Humans.Domain/Entities/`. Three
places had to agree and only one was changed. The marker is now correct, and the catalog entry carries
a note saying which of the three is authoritative, so the next person doesn't have to discover it.

### 3. New sections keep shipping with no freshness marker — second consecutive occurrence

`src/Sections/Humans.Tour/Docs/Tour.md` shipped with **no freshness marker at all**, so the newest
section was invisible to the sweep. `Humans.Holded` did exactly the same thing last sweep. Two in a
row makes this a template gap rather than an author oversight: neither `SECTION-TEMPLATE.md` nor
`G5-SECTION-TEMPLATE.md` instructs a *new* section doc to carry a marker (the G5 template only covers
rewriting triggers for a *moved* doc). Marker added to `Tour.md` by hand. **See Questions.**

---

## Updated automatically

### Mechanical

| Entry | Result |
|---|---|
| `dev-stats` | script — appended 2 rows, table now 144 data rows |
| `reforge-history` | script — ok=2 fail=0, CSV at 133 distinct days |
| `authorization-inventory` | regenerated — new Tour section (AllowAnonymous); paths updated for the #1280 and #1269 moves; no role/policy drift anywhere in `src/Sections/**` |
| `controller-architecture-audit` | regenerated — added `TourController`, count 91 → 92; `HumansTeamControllerBase` relocation noted |
| `service-data-access-map` | regenerated (+203/−97) — `HumansDbContext` removal propagated; per-table context ownership re-resolved from source |
| `dependency-graph` | verified — every service ctor re-audited; zero edge changes, G5-batch-3 note added |
| `docs-readme-index` | regenerated — 2 rows added (Development, Tour), 1 retargeted (Onboarding → in-project path); all 154 links resolve |
| `about-page-packages` | **verified clean** — every production `PackageReference` already matches `Directory.Packages.props` |
| `code-analysis-suppressions` | **verified clean** — the `tests/Directory.Build.props` churn was compile-item conditions, not `NoWarn` |
| `data-model-index` | **regenerated after a false clean — found a real bug**, see below |
| `guid-reservations` | re-checked after the same false clean — genuinely clean this time by inspection |

### Editorial — highest-value content fixes

Ten lanes, 135 docs. The fixes that change what a reader would do:

- **`docs/guide/LegalAndConsent.md`** — told Consent Coordinators that **Flag removes the human from
  Volunteers/Colaborador/Asociado teams**. `OnboardingService` carries an explicit comment that Flag is
  now annotation-only and "no longer deprovisions any team"; `RejectSignupAsync` is the real kick-out
  lever, and Reject is a third button on the same page. Also corrected the queue's entry gate (it is
  "legal name entered and not yet cleared", not "signed all required global documents"). Rewritten as
  three actions: Clear / Flag / Reject. This doc had been unswept for months.
- **`docs/features/global/background-jobs.md`** — largest single-doc drift. Two jobs documented as
  DISABLED are registered unconditionally by `RecurringJobExtensions.cs` (hourly / daily 3 AM); two
  retired jobs still listed; three real jobs undocumented; two wrong cron schedules
  (`SendReConsentReminderJob` is 4:00 not 6:00 AM; `TermRenewalReminderJob` and `CleanupEmailOutboxJob`
  are weekly, not daily).
- **`docs/features/cantina/daily-roster.md`** — claimed 403 Forbidden in five places; the app 302s to
  `/Account/AccessDenied` (`Program.cs:153`, no `OnRedirectToAccessDenied` override).
- **`src/Sections/Humans.Events/Docs/{Events,Events-feature}.md`** — 13 occurrences of role
  `GuideModerator`, which exists **nowhere in code**; the real role is `EventsAdmin` /
  `PolicyNames.EventsAdminOrAdmin`.
- **`src/Sections/Humans.Store/Docs/Store.md`** — stated "Contracts/ is empty because nothing consumes
  Store" and "Service has no interface". Both false: `IStoreServiceRead` landed in #1264.
- **`docs/features/profiles/contact-fields.md`** — documented `IsOAuth` and `IsNotificationTarget` as
  live C# properties; `IsOAuth` is shadow-only, the field is `IsPrimary`, and `Provider`/`ProviderKey`
  replaced the OAuth bool.
- **`docs/features/google-integration/google-integration.md`** — wrong config key
  (`AllowLeadsToManageResources` → `AllowCoordinatorsToManageResources`), wrong route base, a phantom
  `Resources/LinkFile` route that does not exist, two undocumented real routes added.
- **`docs/features/shifts/coordinator-roles.md`** — stale `[Authorize(Roles=…)]` block, wrong policy
  name, and a whole section describing `AdminController.CanManageRole()` — both gone; replaced with the
  real `RoleAssignmentAuthorizationHandler` mechanism.
- **`src/Sections/Humans.Gdpr/Docs/Gdpr.md`** — `IUserDataContributor` split was 8/13, actually 4/17
  (total 21 unchanged).
- **`src/Sections/Humans.Containers/Docs/Containers.md`** — invented `main-{guid}`/`placement-{guid}`
  filename prefixes; code uses one `{guid}.{ext}` key.
- **`docs/sections/_Index.md`** — missing its Mailer row entirely.
- **`docs/features/shifts/shift-preference-wizard.md`** — route `/Profile/ShiftInfo`, actually
  `/Profile/Me/ShiftInfo` (5 occurrences).
- **`src/Sections/Humans.Issues/Docs/Issues.md`** — `AllKnownSections` missing Scanner.
- **`docs/features/auth/authentication.md`** — Section Admin Roles table missing `StoreAdmin` and
  `EETeamAdmin`.
- **`docs/features/test-system-reliability.md`** — "85 Application test files" reproduced by no
  counting method (real figure 64 repo-wide, 15 in `Humans.Application.Tests`); passage also still
  named the deleted `HumansDbContext`.
- **`docs/seed-data.md`** — cited a migration file absorbed into a section baseline and no longer
  present; two configurations moved into their section projects.
- **`docs/architecture/design-rules.md`** — 12 fixes, the largest being the G5 section list (29 → the
  actual 35), the design-time-factory list (now the five that really remain in
  `Humans.Infrastructure.Data`), and §8's preamble rewritten around the deleted root context. §15i's
  `HumansMetricsService` entry was wrong in a load-bearing way: it no longer injects a `DbContext` at
  all. §5's decorator example named `CachingProfileService` / `ProfilesProfileService` /
  `ProfileSectionExtensions.cs` — neither type exists and that file contains no decorator wiring;
  retargeted at the live `CachingUserService` / `UsersUserService` / `UsersSectionExtensions.cs:35-37`,
  which still uses exactly the keyed-inner + factory-forward shape §5 describes.
- **`docs/architecture/roslyn-analysis.md`** — the intro's shipped range `HUM0001-HUM0020` silently
  included `HUM0004`, which does not exist; replaced with the exact live set (29 rules) plus the full
  retired-id list.
- **`src/Sections/Humans.Notifications/Docs/Notifications.md`** — cited retired `HUM0022`; the rule is
  now enforced by the universal `HUM0025`.
- **`docs/architecture/code-analysis.md`** — next-free-id note omitted `HUM0004` from the retired set.

### Verification passes

Two adversarial passes ran after the lanes finished:

- **Cross-check of the four mechanical regens** — found **4 confirmed errors**, all in
  `service-data-access-map.md`: repository classes named `HoldedRepository`, `HoldedMirrorRepository`,
  `ContainerRepository`, `SystemSettingsRepository`. The G5 convention is a bare `Repository` class
  implementing the named interface. Neither the producing lane nor the doc self-flagged these. (This is
  the second sweep running where this cross-check caught errors no lane reported — it should stay a
  standing step.)
- **Verification of this sweep's own edits** — 47 added factual claims checked against source across
  ~35 docs. **Zero errors.** One claim was flagged unverifiable rather than guessed at and has since
  been corrected: `MailerAudienceSyncJob`'s "Daily 6:00 AM" was sourced from an `e.g.` inside a code
  comment (`RecurringJobExtensions.cs:20`), not from any default — the setting ships empty and the job
  does not run at all by default.
- **Link integrity across 414 `.md` files** — 5 real broken links fixed (two `../../` relative depths
  that should be `../../../../` from `src/Sections/*/Docs/`, a `TeamConfiguration.cs` path that moved at
  G5, and two `docs/sections/Auth.md` links pointing at a doc that moved in-project). 9 further hits
  were regex false positives, not links. Every remaining reference to the 7 deleted husks is a
  `<!-- wheat: -->` provenance comment, which is the intended end state.

### A caution for the next sweep: the orchestrator's own briefing was wrong

I briefed the architecture lane that **HUM0012, HUM0013, HUM0021, HUM0024 and HUM0029 were retired**,
taking it from commit `c1a1076fb`'s title (*"retire HUM0012/13/21/24/29 instead of widening them"*) —
whose body also says it "deletes five analyzers". The lane refused the claim and checked the diff:
`c1a1076fb` deletes exactly **two** analyzer source files, `CrossSectionEfJoinAnalyzer` (HUM0024) and
`ObsoleteCrossDomainNavReadAnalyzer` (HUM0021). HUM0012, HUM0013 and HUM0029 are still live source
files, still in `AnalyzerReleases.Unshipped.md`; the commit only *added doc comments* to them. Verified
independently, and confirmed no doc in this sweep asserts the wrong claim.

Two lessons: **a commit message is not a source of truth — diff it**; and a worker pushing back on the
orchestrator's premise is the check working, not noise.

---

## Pruned

7 husks deleted, **−4,387 lines** (8.6% of 50,747 total doc lines; over the 7% / 3,552-line soft cap —
justification below).

### Wheat migrated

| Source § | Destination | What survived, and what verified it |
|---|---|---|
| `2026-05-25-early-entry-roster.md` §Task 5 | `design-rules.md` §15 | `TrackedCache.GetAsync` only calls `Set` on a non-null load, so it never caches a not-found result; caching a legitimate negative needs manual `TryGet`/`Set`. Verified against `TrackedCache.cs` and the shipped `CachingEarlyEntryService.GetForUserAsync`. |
| `2026-05-27-coordinator-availability-on-profile.md` §Deviations | `docs/sections/Shifts.md` | `SetDayAvailabilityAsync` guards only `dayOffset >= 0` while `SetDayOffAsync` validates the full window; inert because every render loop is bounded to `[BuildStartOffset, 0)`. Verified against current `VolunteerTrackingService.cs`. |
| `2026-06-06-account-merge-consolidation.md` §Deviations | `conventions.md` §Transaction Boundary | **A correction, not an addition.** The doc's cited example was factually wrong: `AccountMergeService.MergeAsync` has no `TransactionScope` (`AccountMergeService.cs:92-212`); only `RejectAsync` does (`:228`). Replaced, and the ordered-no-transaction rule (idempotent steps + a single observable commit point) documented alongside it. |

### Husks deleted

| File | Lines | Reason |
|---|---|---|
| `docs/plans/2026-06-11-q3-ui-refactoring-plan.md` | 243 | Wheat already in `conventions.md`; rest is a file:line audit inventory + 5-phase task list |
| `docs/plans/2026-06-27-post-event-app-feedback-survey.md` | 171 | Wheat already in `Surveys.md`; its remaining §1.1 claim is **no longer true** — `SurveyAudienceType.LoggedInSince=4` now exists and tracking issue #894 is closed |
| `docs/superpowers/plans/2026-05-25-early-entry-roster.md` | 1,367 | Shipped TDD plan; its sequential-fan-out rationale is explicitly superseded by design-rules §8a post-#858 |
| `docs/superpowers/plans/2026-05-27-coordinator-availability-on-profile.md` | 757 | Mined (above); remainder is task list + two pure implementation-mechanics deviations |
| `docs/superpowers/plans/2026-06-04-survey-section.md` | 694 | All 10 rationale items already in `Surveys.md` at equal or greater depth |
| `docs/superpowers/plans/2026-06-06-account-merge-consolidation.md` | 538 | Mined (above); rationale already in `docs/sections/Users.md` |
| `docs/superpowers/plans/2026-06-09-team-early-entry-ticket-lookup.md` | 617 | Small shipped feature; notes are single-endpoint implementation detail, below the genre bar for all three allowed destinations |

**Cap overage (8.6% vs 7%), deliberate.** Two husks were mined this sweep and cannot be stranded per
the skill's own exception. The other two were fully analyzed and verified all-chaff; deferring them
would discard completed verification for zero reviewability gain, since all seven are whole-file
deletes a reviewer skims as seven lines of diff.

### Refs retargeted

- `docs/plans/2026-06-13-q3-transition-plan.md:5` → the deleted Q3 UI plan rewritten as historical,
  pointing forward to `conventions.md`.

### Deliberately kept

- `docs/plans/2026-06-13-q3-transition-plan.md` — past its age-out date but still actively cited by
  `2026-08-03-*` and `2026-08-07-*` plans and by the G5 split design as the gate-ladder definition.
  Load-bearing, not a husk.
- `docs/architecture/tech-debt-2026-04-23.md` — `debt-ledger.yml:321` conditions its retirement on all
  open items being ledgered or done; it still lists genuinely open items.

---

## Flagged for human review

**None.** Every concrete contradiction found this sweep was verified against source and fixed inline.
Three items that lanes initially escalated as out-of-scope were verified and fixed rather than queued:
the `GuideModerator` role, the `StoreAdmin`/`EETeamAdmin` role rows, and the `contact-fields.md`
`IsOAuth` block.

## Proposed for review

**None — all prune candidates resolved this sweep.**

## Questions — all four asked inline and answered in-session

1. **`P4` of `test-system-reliability.md` contradicts the EF-InMemory rule.** → **Leave it.** No edit
   made; P4 stands as written.
2. **Repair `docs/scripts/freshness-checks/diff-mode.sh`?** → **Yes, and point it at the section docs
   going forward.** Done:
   - **New test 7** — expands every `freshness:triggers` glob in every editorial doc and fails on any
     that resolves to nothing, calling out fully-dead docs by name. This is the only check that can see
     the failure mode; tests 1-6 passed throughout the five sweeps it kept recurring.
   - **Editorial walk is now derived from the catalog** by a shared `editorial_docs()` helper used by
     tests 3, 4 and 7, so the check cannot drift away from `editorial_trees`. It previously covered only
     `docs/{sections,features,guide}`, missing the **36 in-project section docs**; a first pass at the
     fix hardcoded four directories and still skipped the **6 individually-listed catalog files**
     (`design-rules.md`, `conventions.md`, `code-review-rules.md`, `coding-rules.md`,
     `roslyn-analysis.md`, `seed-data.md`) — caught in review by Codex on PR #1283. Reading the list from
     the catalog fixes both at once: **141 docs checked**, up from 135, up from 99 before the sweep.
   - **Test 5's synthetic probe repointed** to `src/Sections/Humans.Teams/Controllers/TeamController.cs`,
     plus a guard that fails loudly if the probe path itself ever stops existing. Both its old paths
     were dead: the probe *and* `docs/sections/Teams.md`.
   - **Test 1's `N_TREES` was always 0** — its awk range closed on the `editorial_trees:` line itself,
     so it reported "0 editorial trees" against a catalog listing ten. Fixed; now reports 10.
   - **Result: 7/7 pass.** This morning's tree would have failed tests 5 and 7.
3. **Add the marker requirement to the section templates?** → **Yes.** Done: `SECTION-TEMPLATE.md`'s
   canonical shape now opens with both markers, with a note on why. `G5-SECTION-TEMPLATE.md` step 7b
   only ever described *moving* an existing doc's triggers, so a section born directly in
   `src/Sections/` fell straight through it — that is precisely how Holded and Tour shipped unmarked
   one sweep apart; it now covers authoring a new in-project doc.
4. **`/Holded` missing from `AdminNavTree`.** → **Add it to the Money section.** Done: one row in the
   `Money` group, `HoldedController.Index` behind `PolicyNames.FinanceAdminOrAdmin` (matching the
   controller's own `[Authorize]`). `Humans.Web` builds clean. Note this is the sweep's **only source
   change** — everything else in this PR is docs.

### A catalog gap the repaired verifier found immediately

Repointing test 5 at a section controller made it fail: **a change to any of the 58 controllers under
`src/Sections/` marked zero mechanical entries dirty.** `controller-architecture-audit` documents all
92 actions including those, and `authorization-inventory` lists their `[Authorize]` attributes, but
both only triggered on `src/Humans.Web/Controllers/**`. Both entries now also trigger on
`src/Sections/*/Controllers/**/*.cs`. Test 5 passes with 2 mechanical + 3 editorial dirty.

That is the repair paying for itself within a minute of being written — the gap was invisible to every
check that existed before it.

## Skipped (errors)

None. All 11 mechanical entries and all 135 dirty editorial docs were processed.
