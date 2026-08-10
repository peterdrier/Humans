# Freshness Sweep — 2026-08-10

| | |
|---|---|
| Mode | diff |
| Previous anchor | `80f14b801` |
| New anchor | `04d2490f0` |
| Commits in window | 33 (30 non-merge) |
| Changed files in window | 1,108 |
| Worktree base | `origin/main` @ `2ca3d5241` (frozen) |
| Mechanical entries dirty | 11 |
| Editorial docs dirty | 125 (+3 recovered, see *Systematic findings*) |

The window is dominated by four refactors: **#866 (G5)** moved six sections into their own
projects under `src/Sections/`; **#858** peeled thirteen more per-section DbContexts out of
`HumansDbContext`; **#992/#996** cut all 54 cross-section FK constraints and stripped the last
cross-section EF navigation properties; **#809** replaced direct `EventSettings` reads with
`IBurnSettingsInfo`. Most drift this sweep is a consequence of one of those four.

## Systematic findings

**37 dead `freshness:triggers` globs across 8 docs.** Every one pointed at a path killed by the
G5 moves or the `Humans.UI` extraction. Two docs — `docs/guide/Events.md` and
`docs/guide/Expenses.md` — had *every* trigger dead, so they had silently stopped firing and had
not been drift-checked for several sweeps. Globs repaired against real paths
(`src/Sections/Humans.<Section>/**`, `src/Humans.UI/...`), blocks reformatted, and a full
content pass run over the recovered docs. A glob-aware verifier now reports **0 dead paths**
across all 131 trigger-bearing docs.

This is the second sweep running to find a large batch of dead triggers (last sweep: 64 across
44 docs). The failure is silent by construction — a doc with a dead glob never appears in the
dirty list, so it looks *clean* rather than *unchecked*. Worth a CI check.

**A doc-set duplicate resolved.** `docs/features/audit-log/audit-log.md` carried a
hand-maintained copy of the `AuditAction` catalog that had fallen 40+ values behind the
`freshness:auto` block in `docs/sections/AuditLog.md`. Deleted the copy in favour of a pointer —
a second auto-block on the same source would just recreate the drift.

## Updated automatically

| Entry | Summary |
|---|---|
| `dev-stats` | script; 2 rows appended (table now 141 data rows) |
| `reforge-history` | script; CSV now 130 distinct days |
| `data-model-index` | Store entity names corrected to their post-G5 class names (`StoreProduct`→`Product` etc.) |
| `controller-architecture-audit` | 90 controllers (was 89): G5 split `FinanceController` into `BudgetAdminController` (Budget CRUD, stays in Web) + a Holded-only `FinanceController` in `src/Sections/Humans.Finance`, which gained `UnbindCreditor` and `ResyncCreditorLedger` |
| `dependency-graph` | two missing edges: `DashboardService → IBurnSettingsService`, and a **new cycle #6** — `CampService` holds `Lazy<ICityPlanningService>` to delete polygon rows on camp deletion while `CityPlanningService` eagerly injects `ICampServiceRead` |
| `service-data-access-map` | 14 sections repointed to their peeled DbContexts; intro context table 9→24; 6 stale G5 folder paths; #992/#996 note added |
| `authorization-inventory` | regenerated across `src/Humans.Web`, `src/Sections`, `src/Humans.UI` |
| `guid-reservations` | 2 stale source links (blocks `0002`, `0026` moved under G5) |
| `code-analysis-suppressions` | verified clean — already matched both `Directory.Build.props` |
| `docs-readme-index` | index tables already complete (no dead links, G5 in-project docs pointed at correctly); fixed the *Historical Design Records* description of `docs/plans/`, which still said "semantic versioning, Google Groups" against contents that are now the Q3 gate ladder, G0 audit, demolition/frozen-section inventories, section DAG and FK-cut inventory |
| `about-page-packages` | package versions refreshed against `Directory.Packages.props` + all `.csproj` |

## Editorial drift fixed

57 docs: `docs/sections/` (24), `docs/features/` (21), `docs/guide/` (7),
`docs/architecture/` (4), plus `docs/seed-data.md`. Dominant themes:

- **#992/#996 propagation** — the largest class by far. Docs across Shifts, Teams, Camps, Email,
  Notifications, GoogleIntegration, Feedback, Issues, AuditLog, Calendar, Consent, Governance and
  Campaigns still described cross-section FK constraints, `ON DELETE SET NULL`/`Cascade`
  behaviour, `.Include(...)` chains, or navigation properties that no longer exist. All corrected
  to bare-Guid columns with in-memory stitching via the `I*ServiceRead` surfaces.
- **#858 DbContext ownership** — `IDbContextFactory<HumansDbContext>` corrected to the peeled
  per-section context in Email, Notifications, Auth, Calendar and others.
- **G5 / `Humans.UI` path drift** — moved types recited at their old paths.
- **#809** — `Shift.GetShiftPeriod` now takes `IBurnSettingsInfo`, not `EventSettings`.
- **#1241** — `HoldedSyncJob` trailing-window behaviour; Holded's `AddHoldedSection` renamed
  `AddHoldedConnector`.

### Escalated by agents, then verified and fixed inline

Every one of these was reported as "flag for a later pass". Each was a concrete, code-checkable
contradiction, so it was fixed this sweep rather than queued:

| Doc | What was wrong |
|---|---|
| `guide/CityPlanning.md` | documented `/CityPlanning/Admin` and a single map page; real routes are `/CityPlanning`, `/CityPlanning/BarrioMap`, `/CityPlanning/ContainerMap/{year}`, `/CityPlanning/BarrioMap/Admin` |
| `features/city-planning/city-planning.md` | cited `/js/container-map/` and a container-map `measure.js` import; both resolve under `/js/city-planning/` |
| `sections/Profiles.md` ×2, `sections/Users.md` ×2, `features/profiles/dietary-medical-nudge.md` | credited `ProfileService` with six GDPR export slices; `UserService` absorbed profile storage in #745 and `ProfileService` implements no contributor interface |
| `architecture/design-rules.md` §8b | "Two fanouts exist today" — there are three (`IEarlyEntryProvider`: Camps + Shifts + Teams) |
| `features/global/administration.md` | full route table, area-split table and dashboard table for `BoardController` / `/Board/*`, deleted in #499. Board survives as a *role*, not an area |
| `sections/Governance.md` ×2, `guide/Governance.md` ×2 | claimed term expiry never resets `Profile.MembershipTier`; `DowngradeMembershipTierForExpiredAsync` resets it (to another still-active tier, else Volunteer) and audits `TierDowngraded` |
| `features/shifts/shift-signup-visibility.md` | `ShiftSignup.User` nav (stripped in #541) and a `ShiftSignupInfo` sample with a `ProfilePictureUrl` field that does not exist |
| `features/cantina/daily-roster.md` | stray "two access paths compose with OR" contradicting the no-team-heuristic rule directly above it |
| `sections/Tickets.md` | missing `/Tickets/Admin/Onsite` (`ScannerAccess`) routing row |
| `sections/Mailer.md` | missing `POST /Mailer/Admin/SyncAll` and `POST /Mailer/Admin/Refresh` |
| `features/governance/asociado-applications.md` | `Application` block missing `SignificantContribution`, `RoleUnderstanding`, `ReviewStartedAt`, `RenewalReminderSentAt` |

## Pruned

5 husks, **−3,039 lines** (5% target = 2,925; 7% cap = 4,095 of 58,514 total doc lines).

| Husk | Lines | Why it was all chaff after mining |
|---|---|---|
| `superpowers/plans/2026-06-02-team-early-entry.md` | 1,143 | body describes a shape that never shipped (art-specific labels, `EarlyEntryArtAdmin` role); as-built amendments already in `Teams.md` |
| `superpowers/plans/2026-05-20-camp-roster-roles.md` | 886 | end state recorded in `Camps.md`; the `camp_leads` world it plans against was dropped in #774 |
| `superpowers/plans/2026-06-16-voluntell-past-shifts.md` | 408 | absorbed into `Shifts.md`, and its split-button view design is superseded by the shipped unified Manage control |
| `superpowers/plans/2026-06-05-e2e-auth-state-reuse.md` | 318 | fully shipped; every code sample is now the real file, with guards the plan never had |
| `superpowers/plans/2026-06-24-cantina-arrival-and-fodmap.md` | 284 | absorbed into `Cantina.md` + `daily-roster.md`; FODMAP gone from `DietaryOptions` with zero references left |

### Wheat migrated

All six verified against current source before migration:

| Wheat | → | Verified by |
|---|---|---|
| Camp registration persists camp + season + creator `CampMember` + Lead `CampRoleAssignment` in one `CreateCampAsync`; unseeded role catalogue degrades to member-only + warning, never blocks registration | `sections/Camps.md` | `CampService.CreateCampAsync` |
| `EETeamAdmin` is board-grantable but deliberately **not** in `AnyAdminRole` — omission is the design | `sections/Teams.md` | `RoleNames.cs`, `AuthorizationPolicyExtensions.cs` |
| Contributor forwarding lifetime: forward the caching decorator only when the fanout read comes off the cached projection (Camps), else forward scoped (Teams, Shifts) | `architecture/design-rules.md` §8b | `CampsSectionExtensions.cs:45`, `TeamsSectionExtensions.cs:30`, `ShiftsSectionExtensions.cs:59`, `CachingCampService.GetEarlyEntriesAsync` |
| A bare-Guid cross-section FK column gets no index for free — #992 removed the EF auto-index with the constraint, so filtered/sorted columns need explicit `HasIndex(...)` | `architecture/conventions.md` | `CampMemberConfiguration`, `CampaignGrantConfiguration`, `BudgetAuditLogConfiguration`, `FeedbackReportConfiguration` |
| SPA/Blazor rewrite was considered and rejected; ViewComponent layer is the consolidation surface | `architecture/conventions.md` | `src/Humans.UI/ViewComponents`, existing "server-rendered Razor is the default" rule |
| Three survey authoring invariants: empty option `Value` ⇒ unanswerable; no free-text flag on options; anonymity chooser pre-selects Identified | `sections/Surveys.md` | `SurveyWizardFlow.cs`, `SurveyQuestionOption`, `SurveyViewModels.cs`, `Survey/Intro.cshtml` |

No inbound refs needed retargeting — the deleted husks were referenced only by the
`<!-- wheat: -->` provenance comments this sweep added.

## Flagged for human review

None. Every escalated item was a concrete code-checkable contradiction and was fixed inline
(see the table above).

## Proposed for review

None — all candidates resolved this sweep.

Two husks were analysed and **deliberately kept**: they are live documents, not history. Both
were left unmodified. Neither is a prune candidate for future sweeps until its work completes —
age alone does not make them husks.

## Questions

All three answered inline by Peter on 2026-08-10:

1. **`2026-06-13-q3-transition-plan.md`** — *keep as the plan of record* until the gate ladder is
   complete, then reconsider. Not deleted. It remains the cited source for gate definitions in
   five surviving plan docs and `NoDestructiveMigrationOps.baseline.txt:74`.
2. **`2026-06-11-q3-ui-refactoring-plan.md`** — *keep for follow-up implementation*, tracked by a
   single issue covering the whole plan. That issue already exists and is open:
   [nobodies-collective/Humans#861](https://github.com/nobodies-collective/Humans/issues/861)
   ("Execute the Q3 UI refactoring plan"), which links the plan doc and carries its five-phase
   breakdown. No new issue needed; nothing deleted.
3. **The post-event app feedback survey has not been sent yet**, but likely will be soon.
   `2026-06-27-post-event-app-feedback-survey.md` is kept — it holds the only copy of the EN/ES
   question set. (Its three engine invariants were still migrated to `sections/Surveys.md`, which
   is additive and independent of the survey content.)

## Skipped (errors)

None.

## Notes

- Six subagents went idle without returning their JSON payload (fourth sweep running with this
  failure). Worked around by having agents write payloads to files and by verifying delivered
  work from the worktree diff; two agents were re-sent and completed their remaining docs.
- The freshness catalog's `editorial_trees` does **not** cover `src/Sections/**/Docs/*.md`, so
  the six G5 sections' in-project invariants docs are outside the sweep. Worth adding.
