# Freshness Sweep Report — 2026-06-13

**Anchor:** `afa6ac5cc` → `18c308a52` (upstream/main HEAD)
**Worktree base:** `origin/main` @ `18c308a52` (origin and upstream were in sync at sweep start)
**Mode:** diff (batch)
**Changed files in range:** 218
**Dirty entries processed:** 9 mechanical + 63 editorial = 72
**Outcome:** 16 docs updated · 5 husks pruned (3,874 lines) · rest verified clean · 0 errors

The bulk of the 218-file range was **mass i18n view localization** (#987 — ~50 views + enums + 3,700 translations) and a **controller-audit compliance refactor** (#994–997 — business logic pushed controller→service, some action moves). Neither changes documented behavior, so most triggered editorial docs verified clean. The genuine behaviour changes were Camps early-entry consumption (#985), the Debug HttpErrors buffer (#993), Scanner manual barcode entry (#986), the gate-terminal onsite roster (#991), and the Profile unlink lockout guard (#1000).

## Updated automatically

### Mechanical
- **dev-stats** — appended the 2026-06-13 codebase-growth row (script).
- **reforge-history** — appended the 2026-06-13 semantic snapshot row (script).
- **controller-architecture-audit** — verified all 87 controllers against source; no action-level drift since 2026-06-12; header date → 2026-06-13.
- **authorization-inventory** — added `GovernanceBoardVotingController.Finalize` (AdminOnly override), `VolunteerTrackingController.SetAvailabilityDay/ClearAvailabilityDay` (VolunteerTrackingWrite), and a `ProfileController` PrivilegedSignupApprover call site; refreshed §6 call-site line numbers.
- **dependency-graph** — 3 edge corrections: removed `OnsiteRoster→ShiftMgmt`; added `EventService→User` and `EventService→Email`; added `Team⇢GoogleSync` (lazy).
- **service-data-access-map** — regenerated the service→repo→table + cache-key map from current source (largest change): removed `GeneralAvailabilityService` (absorbed into `VolunteerTrackingService`), merged `IShiftSignupRepository` into `IShiftManagementRepository`, added `ICalendarFeedContributor` to `ShiftSignupService`, documented `UnsubscribeService`'s `IUserServiceRead`.
- **docs-readme-index** — added the missing `features/debug/http-errors.md` index entry; all other rows verified.

### Editorial drift-fix
- **sections/Camps.md** — 3 early-entry-consumption fixes (#985): `HasEarlyEntry` retained on `Removed` when the holder already entered; `GetGrantedCountForSeasonAsync` no longer filters `Status=Active`; new `MemberAlreadyEntered` negative-access rule.
- **sections/Debug.md** + **features/debug/client-stats.md** — added `/Debug/HttpErrors` + `/Debug/Translations` routes (#993); corrected `IClientStatsTracker` signature; documented the error-buffer workflow.
- **sections/Scanner.md** — manual barcode entry added to the `/Scanner/Tickets` concept (#986).
- **sections/Events.md** — 3 attribution fixes (lifecycle email orchestration moved `EventsModerationController` → `EventService`).
- **sections/Governance.md** — removed the deleted `HasBoardVotesAsync` (inlined as a NoVotes guard).
- **sections/Onboarding.md** — added `IConsentServiceRead` + `IHumanLifecycleService` cross-section dependencies.
- **guide/Admin.md** — added the `/Debug/HttpErrors` diagnostic page.
- **sections/Profiles.md** — corrected the CV-entries write path (`IProfileService.SaveCVEntriesAsync` → `IProfileEditorService.SaveProfileAsync`).

## Verified clean (dirty but no drift)
- **Mechanical:** `data-model-index` (Event.cs gained only computed/non-persisted members — the DB-column entity-index is unchanged); `code-analysis-suppressions` (only `SatelliteResourceLanguages` `en;es`→`en;es;ca;de;fr;it` changed; no NoWarn/analyzer change).
- **Editorial clusters with no contradictions:** Teams (4 docs), Google-integration (4), Expenses (2), Profiles-email (4), misc/global/shifts (12), and the architecture rule docs (conventions, code-review-rules, design-rules, roslyn-analysis). Compliance refactors moved code *toward* the documented rules, so the rules themselves were not contradicted.

## Pruned
| Husk | Lines | Reason |
|------|------:|--------|
| docs/plans/2026-05-13-section-align-budget.md | 102 | all chaff (executed inventory checklist) |
| docs/plans/2026-05-13-section-align-campaigns.md | 179 | all chaff (clean-pass checklist) |
| docs/plans/2026-05-13-section-align-events.md | 1015 | all chaff (pre-impl plan for #539) |
| docs/plans/2026-05-13-section-align-teams.md | 167 | all chaff (findings since remediated) |
| docs/superpowers/plans/2026-05-13-ticket-attendee-contact-import.md | 2411 | all chaff (fully-executed TDD plan) |

Total **3,874 lines = 4.9% of docs/** (under the 7% cap). No inbound references from living docs (only `last-report.md`, overwritten this sweep, and a self-ref).

### Wheat migrated
**None.** Every candidate was a fully-executed plan whose durable decisions already live in the corresponding `docs/sections/*.md` (verified against current source — e.g. the section-align-teams cross-section findings, `AuditLogRepository`/`HumansMetricsService` reading `Teams`, are gone from the code).

## Flagged for human review
- **Unmarked editorial docs (no `freshness:triggers` → sweep blind spots, not reviewed):** `features/{26-events, 27-guide-browser, 43-google-group-membership-sync, test-system-reliability, user-search-overhaul}.md`, `features/agent/agent-section.md`, `features/scanner/{gate-terminal-login, scanner-barcode}.md`, `guide/{AiHelper, EmailAccount, SigningIn, TicketTransfers, TwoStepVerification, YourData}.md`, `sections/{Agent, Mailer, _Index}.md`. Two are **likely stale**: `features/scanner/gate-terminal-login.md` (#991 gate-terminal onsite roster) and `features/scanner/scanner-barcode.md` (#986 manual barcode) describe behaviour that changed this range but did not fire. → raised inline.

## Proposed for review
None — all candidates resolved this sweep.

## Questions (raised inline with Peter)
1. **sections/Users.md** caching note lists sign-in `LastLoginAt` among Identity-machinery writes caught by `UserInfoSaveChangesInterceptor` — but it now routes through `IUserService.RecordLoginAsync` → `repo.SetLastLoginAsync` (a service/repo write, not `UserManager.UpdateAsync`). Reword the attribution, or leave (the interceptor likely still fires on the repo's context)?
2. **guide/Expenses.md** says "confirm once you've sent it — that moves those reports to SEPA sent," but generating the batch is one operation (`GenerateSepaPayoutAsync`) that flips reports to `SepaSent` immediately. Tighten the user-facing wording? (`sections/Expenses.md` is accurate.)
3. Add `freshness:triggers` to `features/scanner/gate-terminal-login.md` + `scanner-barcode.md` and drift-fix them for #986/#991 now, or defer to a dedicated pass?

## Skipped (errors)
None.
