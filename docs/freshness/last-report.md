# Freshness Sweep — Report

**Run:** 2026-05-31 (worktree `freshness-sweep/2026-05-30T230606Z`)
**Mode:** diff (batch)
**Previous anchor:** `cd9a9345` (`cd9a93458`)
**New anchor:** `e2b9630e` (`e2b9630e4`, `upstream/main` HEAD)
**Worktree base:** `origin/main` `e2b9630e4` (in sync with `upstream/main`)
**Diff scope:** 281 changed files since the last anchor.

The range is dominated by Reforge-guided **section surface reductions** (Budget #836, GoogleIntegration #835, Users #838, Email #837, Tickets #833, Expenses #830) and the **Shifts service/repository refactor** (#820) — behaviour-preserving by design — plus genuinely behavioural changes: **Email `IEmailService`→single `SendAsync(EmailMessage)` collapse** (#844), **Store TeamsAdmin order access** (#845) + **VAT-inclusive pricing** (#839), and the **admin sidebar realign** (#842).

---

## Updated automatically (mechanical)

- **dev-stats** — regenerated (`docs/development-stats.md`, script).
- **reforge-history** — regenerated incrementally: 2 day-rows appended through HEAD `e2b9630e4`, 0 build failures (`docs/reforge-history.csv`, script).
- **authorization-inventory** — Store team-order authz (#845) reflected (TeamsAdmin / `isPrivilegedReader = CanAdministerStore || IsTeamsAdmin`, camp-vs-team split, `Delete` op); `IbanAccessHandler` documented as registered-but-uninvoked; call-site line-numbers re-anchored.
- **controller-architecture-audit** — no net change (all 80 controllers/actions match HEAD); date bump + stale "newly added" paragraph reworded.
- **service-data-access-map** — corrected accumulated dep-list drift: `BudgetService`→`IUserServiceRead`, `ShiftSignupService` repo list, dropped phantom injections (`IMembershipCalculator`, `IEarlyEntryInvalidator`, `IEmailRenderer`), `OutboxEmailService` single `SendAsync`, `GoogleWorkspaceSyncService` deps.
- **dependency-graph** — removed the `ShiftSignupService → IMembershipCalculator` edge (no longer injected); read-splits collapse onto the owning-service node per the diagram convention, so no edge changes; `linkStyle` indices recomputed.
- **docs-readme-index** — added the one missing row (`sections/_Index.md`); 0 removed, 0 description changes.
- **data-model-index** — verified no-op: every current `Domain/Entities` type is already indexed with the correct owning section; no entity added/removed in range.
- **code-analysis-suppressions** — verified no-op: the block already matches current `NoWarn` (`CS1591;MA0048;MA0016;MA0026;MA0051;VSTHRD200;HUM_PROFILE_ISSUSPENDED;HUM_USER_NORMALIZEDEMAIL` + tests `xUnit1051;HUM_USER_DISPLAYNAME`). The in-range `Directory.Build.props` change only dropped `HUM_USER_GetById` from `WarningsNotAsErrors` (never in this block).

Not dirty (no trigger match): **about-page-packages**, **guid-reservations**.

## Editorial drift-fix

Every triggered `flag-on-change` doc was reviewed against the *specific* changed files its triggers matched and **fixed in place** (≈42 surgical edits across 21 docs). Highlights:

- **Email (#844 collapse):** `IEmailService.Send<X>Async` → build an `EmailMessage` via `IEmailMessageFactory` + the single `IEmailService.SendAsync` — fixed in `sections/Email.md` (×4), `features/email/email-outbox.md`, `features/google-integration/google-removal-notifications.md`, `features/shifts/email-a-rota.md`, plus the same collapse named in `sections/{Governance,Onboarding,Campaigns,Feedback,Issues,GoogleIntegration}.md` and `features/google-integration/workspace-account-provisioning.md`.
- **Store (#845 / #839):** TeamsAdmin order authz (view-any + manage team orders only; never Pay/EditCounterparty; `Delete` op confirmed against `StoreOrderAuthorizationHandler`) and VAT-inclusive pricing — `sections/Store.md` (×3), `features/store/store.md` (×2), `guide/Store.md` (×2).
- **Shifts (#820):** `GetStaffingDataAsync`/`GetStaffingHoursAsync` → `GetStaffingSnapshotAsync`; `GeneralAvailabilityService` deleted (folded into `VolunteerTrackingService`) — removed from owning-services/cache/arch-test lists (all deletions verified by `ls`); `GetShiftsForEventAsync`→`GetEventShiftsAsync`; `ITeamService.GetByIdsWithParentsAsync`→`ITeamServiceRead.GetTeamsAsync` in workload-dashboard.
- **Teams:** `ITeamServiceRead` 4→5 methods (`GetUserCoordinatedTeamIdsAsync` moved onto the read interface).
- **Admin/Notifications (#842):** sidebar group list realigned to sections in `guide/Admin.md`; `IGoogleSyncService`→`IGoogleSyncServiceRead` (×3) in `sections/Notifications.md`.
- **GoogleIntegration (#835):** retired admin-digest subsection → failed-sync meter (`GetFailedSyncEventCountAsync`); `/Google` dashboard nav rewrite.

No drift found (reviewed, clean) in the **Profiles/Users**, **Budget/Tickets/Expenses**, and **Shifts-peripheral/Cantina/VolunteerTracking** clusters — the surface reductions there changed only internal/private surface the docs never cite.

## Pruned

Removed the shipped **email/OAuth decoupling PR sequence + comm-prefs redesign** — 6 husks, **7,264 lines (~6.3 %** of the 114,890-line doc base; within the 7 % cap):

| Husk | Lines | All-chaff reason (durable signal already lives in) |
|---|--:|---|
| `plans/2026-04-28-email-oauth-pr1-decouple-writes.md` | 1,251 | `memory/architecture/no-identity-email-column-reads.md`, `email-mutation-paths.md`, `sections/Profiles.md` |
| `plans/2026-04-29-email-oauth-pr2-identity-surgery.md` | 747 | same atoms; husk's column-drop migration was later **reversed** (shadow properties) |
| `plans/2026-04-30-email-oauth-pr3-useremails-modernization.md` | 1,376 | `sections/Profiles.md` UserEmail table (`Provider`/`ProviderKey`/`IsGoogle`, shadow props) |
| `plans/2026-04-30-email-oauth-pr4-grid-and-link.md` | 2,473 | `sections/Profiles.md`; husk surface later superseded by PR #477 `ReconcileOAuthIdentityAsync` |
| `plans/2026-04-30-email-oauth-pr7-drop-legacy-columns.md` | 40 | never-executed deferred stub; rule in `no-drops-until-prod-verified.md` |
| `plans/2026-04-04-communication-preferences-redesign.md` | 1,377 | `features/profiles/communication-preferences.md` (pre-confirmed by last sweep) |

**Wheat migrated:** none — verified (against current code + the three named memory atoms) that every durable rule is already captured in living docs; migrating would have injected stale method-level surface.
**Inbound refs retargeted:** `specs/2026-04-27-email-and-oauth-decoupling-design.md` §312 — dead link to the deleted PR 7 stub rewritten as a historical note (rule retained inline + atom pointer).

## Freshness-marker maintenance

Added scoped `<!-- freshness:triggers -->` headers to the 5 architecture docs that previously carried `flag-on-change` with **no triggers** (false-firing on every `src/**` change — flagged unactioned by the last two sweeps): `design-rules.md`, `code-review-rules.md`, `coding-rules.md`, `conventions.md`, `roslyn-analysis.md`. No content drift found in any (reviewed conservatively; `design-rules.md` and `roslyn-analysis.md` were already updated in-range by their PRs).

## Flagged for human review

Confirmed concrete drift that lives **outside this sweep's changed-file scope** (pre-existing) or is a genuine code-vs-doc judgment call — escalated, not edited:

1. **`sections/Auth.md` §41 — `CompleteSignup`** still says "creates User + UserEmail". Current `AccountController.CompleteSignup` collects burner + first/last legal name and provisions a **Profile** via `CompleteMagicLinkSignupAsync` (#826, but `AccountController` not in this range). Ready rewrite: "collects burner + first/last legal name, provisions User + UserEmail + Profile via `CompleteMagicLinkSignupAsync`, signs in (double-click safe)". (`guide/Onboarding.md` and `features/auth/magic-link-auth.md` already describe this correctly.)
2. **`sections/Shifts.md` §270/§281 — account-merge reassign trio** names `IGeneralAvailabilityService.ReassignToUserAsync` (interface **deleted** by #820) and the old per-service `Reassign*` names. Rewrite to the `IUserMerge.ReassignAsync` pattern: `ShiftSignupService` / `ShiftManagementService` / `VolunteerTrackingService` each implement it, invoked by `AccountMergeService.AcceptAsync`. (Left unedited — the current merge API needs confirmation before rewriting an invariant doc.)
3. **`features/profiles/dietary-medical-nudge.md` vs `sections/Profiles.md`** disagree on field ownership: Profiles.md (and the now-verified `sections/Shifts.md`) say `DietaryPreference`/`Allergies`/`Intolerances`/`MedicalConditions` **moved to `Profile`**; dietary-medical-nudge.md still places them on `VolunteerEventProfile`. Reconcile dietary-medical-nudge.md against current `Domain/Entities`.
4. **`features/cantina/daily-roster.md` §149** states "There is no per-day route" but `CantinaController` still ships `GET /Cantina/Roster/Day(/Csv)` (documented as current in `sections/Cantina.md`). Code-vs-doc judgment call: fix the doc, or the per-day route is dead code that should be removed.
5. **`IbanAccessHandler`** (Expenses) is registered in DI and unit-tested but has **no production call site** (`ProfileController.RevealIban` gates on `AdminOnly` directly). Dead code — consider a tech-debt issue.
6. **`sections/Expenses.md` §138** cites `CategoryRequiresCoordinatorEndorsementAsync`, which #830 made `internal` (method still exists, behaviour unchanged) — not a contradiction, just a now-internal-method citation. Optional reword.

## Unmarked editorial (add `freshness:triggers` in a future pass)

19 editorial docs carry no freshness markers, so they are invisible to the sweep's dirty-matching: `features/{26-events,27-guide-browser,43-google-group-membership-sync,test-system-reliability}.md`, `features/agent/agent-section.md`, `features/scanner/scanner-barcode.md`, `guide/{AiHelper,EmailAccount,SigningIn,TicketTransfers,TwoStepVerification,YourData}.md`, `sections/{Agent,Debug,Events,Mailer,Scanner,_Index,admin-shell}.md`.

## Future-sweep prune candidates (budget-deferred, not stranded)

Analyzed-confidence-dead shipped plans/specs left for a future sweep to keep this PR's deletions skimmable (would push past the 7 % cap): `ticket-vendor-integration` plan+spec, `store-section`, `issues-section`, `holded-read-integration`, `user-guide`, `in-app-guide`, `admin-shell-left-nav`, `community-calendar-slice1`, `pr235-cache-collapse` (≈ 22k lines). `2026-04-25-freshness-sweep.md` deliberately retained (the sweep's own active design).

## Proposed for review

None — all candidates resolved this sweep.

## Questions

None asked this sweep.

## Skipped (errors)

None.
