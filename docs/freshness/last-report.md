# Freshness Sweep Report — 2026-06-05

**Mode:** diff &nbsp;·&nbsp; **Previous anchor:** `17808b4c` &nbsp;·&nbsp; **New anchor:** `upstream/main` `2f2ab285f` &nbsp;·&nbsp; **Worktree base:** `origin/main` `2f2ab285f`

`origin/main` and `upstream/main` were at the **same commit** at sweep start (a prod promotion, #833, had just landed) — noted, not reconciled; the frozen anchors stand.

## Window summary

Nine non-merge commits since the last sweep. The blast radius was dominated by one **mechanical** change and a handful of **substantive** ones:

- **#892 HUM0030** — centralize date/time format strings into one home. *Mechanical, very wide* (format-string swaps across ~170 files); the new analyzer + helper classes are the only substantive part.
- **#894** — redesign Barrios compliance page as a **role-staffing matrix** (new controller, view, resource-based auth policy). *Substantive.*
- **#889** ("reduce build warnings") — actually folded in **real refactors**: SystemSettings persistence centralized, Google sync-outbox ownership moved into Google Integration, `IDriveActivityMonitorRepository` adapter **deleted**, role-claims routed through the auth service, two analyzer behavior changes (HUM0024 Users/Profiles fold, DbContext bootstrap-boundary allowance), 5 inert `[Grandfathered("HUM0024")]` markers removed. *Substantive.*
- **#887** — Store admin Summary totals row. *Substantive (small).*
- **#882** — converge ShiftSignups + EventSettings reads onto `ShiftRepository` (clears HUM0025). *Substantive (internal data-access only).*
- #890 (docs NoTargets fix), #893 (e2e auth-state reuse), #888 (prior sweep), #891 (sweep skill) — no doc impact.

## Updated automatically (mechanical)

- **dev-stats** — appended 2026-06-05 row (table now 109 data rows).
- **reforge-history** — appended 2026-06-05 snapshot (CSV now 98 rows).
- **service-data-access-map** — #882 (ShiftSignups/EventSettings reads converged onto `ShiftRepository`; `VolunteerTrackingRepository` now owns only `volunteer_build_statuses` + `general_availability`), #889 (drive-monitor repo deleted; **new System Settings section** owns `system_settings`; `GoogleSyncOutboxService` owns outbox writes; Email/Teams route through owning services). Resolved 4 previously-flagged cross-section table reads now that ownership converged. #894 verified data-access-neutral.
- **dependency-graph** — removed deleted drive-monitor repo edge; added `SystemSettings`/`GoogleSyncOutbox` nodes + `DriveMon→SysSettings` and `Team -. lazy .-> GSyncOutbox` edges; relinked lazy-edge `linkStyle`.
- **authorization-inventory** — added #894 `CampComplianceAccess` policy + `CampComplianceAccessHandler` + `CampComplianceController` (§1/§5/§6); corrected drifted line numbers; #889 claims re-sourcing confirmed no-impact.
- **controller-architecture-audit** — added `CampComplianceController` (Compliance moved out of `CampAdminController`) + `DebugController.FormatGallery`; count → 81; date → 2026-06-05.

### Mechanical skipped — verified no-op (evidence)

- **docs-readme-index** — no editorial docs added/removed in the window (`git diff --name-status` shows all-Modified); index current.
- **data-model-index** — no `src/Humans.Domain/Entities/**` changed; the config diffs were `[Grandfathered("HUM0024")]` attribute removals only (no schema change). *Exception:* the `SystemSetting` ownership cell in the auto-block was stale from a **service-level** change the entity/config triggers can't see — fixed inline (see Editorial below) rather than via a full regen that would otherwise reproduce an identical doc.
- **guid-reservations** — no `HasData`/`new Guid(` changes in the config/constants diffs (`SystemSettingKeys` added a string const, not a GUID).
- **about-page-packages** — only `docs/docs.csproj` changed; no `Directory.Packages.props` or production-package change.
- **code-analysis-suppressions** — trigger globs (`Directory.Build.props`, `tests/Directory.Build.props`, `tests/BannedSymbols.txt`) did not fire.

## Updated (editorial drift-fix)

- **docs/sections/Camps.md**, **docs/features/camps/camps.md** — #894 compliance page is now a barrios × roles staffing matrix served by the new `CampComplianceController` under the `CampComplianceAccess` policy (CampAdmin/Admin **or** any team/sub-team coordinator); updated routes, actor tables, acceptance criteria, and trigger blocks.
- **docs/sections/Auth.md** — added the `CampComplianceAccessHandler` to the composite-handlers catalog.
- **docs/sections/Email.md** — pause-flag persistence now via `ISystemSettingsService` (`IsEmailSendingPaused`); processor reads via `IEmailOutboxService.IsEmailPausedAsync`; added SystemSettings cross-section dependency.
- **docs/sections/GoogleIntegration.md** — outbox-enqueue ownership corrected (`TeamRepository`-folded → `IGoogleSyncOutboxService` inside an ambient `TransactionScope`).
- **docs/sections/Shifts.md** — repository-surface bullets corrected: `IVolunteerTrackingRepository` owns only `general_availability` + `volunteer_build_statuses`; the converged `GetEligibleBuildSignups`/`GetConfirmedShiftsInRange` reads live on `IShiftManagementRepository` (HUM0025 markers gone).
- **docs/features/store/store.md** — By-camp card notes the new camps-only footer totals row; Cross-tab card no longer claims row totals / grand total (column totals only).
- **docs/architecture/roslyn-analysis.md** — added **HUM0030** (datetime-format-single-home); recorded the **HUM0024** Users/Profile/Profiles → "Humans" fold + retired markers; next-free-slot corrected to HUM0031.
- **docs/architecture/conventions.md** — added a "Date/Time Formatting" convention (single home + HUM0030).
- **docs/architecture/design-rules.md** *(orchestrator)* — §8 table-ownership map: dropped Email's stale claim to own the `system_settings` key, added a **System Settings** owner row, and rewrote the per-key-ownership note to "owned by the System Settings section, exposed via `ISystemSettingsService`"; corrected the stale key name `email_outbox_paused` → `IsEmailSendingPaused`. *(Concrete #889 drift; design-rules is the regulations doc — see "Surfaced inline".)*
- **docs/architecture/data-model.md** *(orchestrator)* — same `system_settings` ownership correction in the ownership-table cell + the `## SystemSetting` section (heading, prose, key-table header, and key name).
- **docs/features/google-integration/drive-activity-monitoring.md** *(Phase 7.5, at Peter's direction)* — corrected Clean-Architecture layer placement (`DriveActivityMonitorService` lives in Application, not Infrastructure), removed the dead `StubDriveActivityMonitorService` reference (no such class in source), and replaced the stale "conditional credentials" Service-Registration prose with the actual unconditional `services.AddScoped<IDriveActivityMonitorService, DriveActivityMonitorService>()`.

### Editorial verified no-op

`coding-rules.md` (atomized stub — new datetime rule already lives as `memory/` atoms), `design-rules.md`/analyzer-scope (rule stated by domain boundary; fold + bootstrap allowance don't contradict it), `guide/Camps.md`, `guide/Admin.md`, `features/auth/authentication.md`, `features/global/administration.md`, `guide/Email.md`, `features/email/email-outbox.md`, `features/global/background-jobs.md`, `guide/GoogleIntegration.md`, `features/google-integration/google-integration.md`, `guide/Shifts.md`, `features/shifts/shift-management.md`, `features/47-volunteer-tracking.md`.

Plus ~80 further editorial docs that triggered **only** via #892's mechanical format-string swaps (and #889's inert `[Grandfathered]` removals / internal `TeamService` outbox-transaction plumbing) — classified and confirmed to carry no behavioral or structural drift. Substantive vs mechanical was separated by per-commit file attribution, not assumed.

## Pruned

Two age-eligible husks, both **drop_entirely** (no wheat survived migration — verified against code + living docs):

- **docs/superpowers/specs/2026-03-15-ticket-vendor-integration-design.md** (397 lines) — fully superseded by `docs/sections/Tickets.md` + `docs/features/tickets/ticket-vendor-integration.md`. The one durable vendor quirk (TicketTailor mis-applies 10% VAT → recompute locally) is triple-covered (source comment `TicketSyncService.cs:355` + both living docs). The spec's headline scope decision ("Stripe integration out of scope") is a **dead intention** — `StripeService.cs` exists and full Stripe fee-enrichment is the documented design.
- **docs/superpowers/plans/2026-04-25-freshness-sweep.md** (1,539 lines) — shipped 17-task implementation checklist; all durable rationale lives in the spec (`2026-04-25-freshness-sweep-design.md`) and `.claude/skills/freshness-sweep/SKILL.md`, both richer (the skill carries the post-ship #819 stale-base gotcha the plan lacked).

**Wheat migrated:** none this sweep (every durable candidate already in a living doc, verified).
**Inbound refs:** none actionable (only reference is in `docs/freshness/last-report.md`, overwritten each run).

**Prune total:** 1,936 lines (~2.3% of 85,764 doc lines) — under the 5% soft target (4,288) by design: the prior sweep already removed ~18.5k husk lines, so the eligible pile is lean. Future-sweep candidates (age not yet met): `docs/superpowers/plans/2026-05-05-email-problems-page.md` (last touched 27d ago) and the May section-align / events plans.

## Flagged for human review

- **DbContext bootstrap-boundary allowance** (informational) — `ApplicationServiceDbContextInjectionAnalyzer` now allows bootstrap boundaries (`HumansDbContextFactory`, `DatabaseMigrationHostedService`). Its shipped-analyzer catalogue home is `docs/architecture/code-analysis.md` (not a sweep target; already touched by #889), and `design-rules.md` already names those boundaries as legitimate — so **no target doc was contradicted**. No action needed.

## Proposed for review

None — all candidates resolved this sweep.

## Questions

Both surfaced inline to Peter (Phase 7.5) and resolved:

1. **`system_settings` ownership framing** (design-rules.md §8 / data-model.md) — Peter confirmed "System Settings section" is the correct framing for the regulations doc. Edits stand as committed.
2. **drive-activity-monitoring.md pre-existing layer drift** — Peter directed fixing it now; corrected in this PR (see Editorial above).

## Skipped (errors)

None — all mechanical scripts (reforge-history, dev-stats) ran clean (exit 0), and every dispatched subagent returned successfully.
