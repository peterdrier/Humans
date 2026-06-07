# Freshness sweep report

**Run:** 2026-06-07 (UTC)
**Mode:** diff
**Previous anchor:** `2f2ab285`
**New anchor:** `0f52d8a63` (upstream/main HEAD)
**Worktree base:** `origin/main` @ `0f52d8a63` (origin == upstream at sweep start — prod promotion in sync)
**Changed files in window:** 374 across 5 substantive PRs — #902 (GUI reusable-component consolidation, cosmetic), #899 (account-merge consolidation), #881 (name-only access via stored UserState), #898 (Shift Summary by Camp), #901 (move legacy admin routes to section homes).

## Summary

- **Mechanical entries:** 7 updated, 2 verified no-op, 2 not dirty.
- **Editorial docs:** 81 dirty (trigger-matched); 19 drift-fixed, 62 verified accurate.
- **Pruned:** 2 husks, 4,226 lines (5.0% of pre-prune total 84,893). No wheat stranded.
- **Review items:** 11 surfaced inline (Phase 7.5) — **all approved by Peter and fixed in this PR** (3 in-scope judgment calls, 6 pre-existing out-of-scope drifts, 2 unmarked docs given `freshness:triggers`).

## Updated automatically (mechanical)

- **dev-stats** — appended 2 daily rows (2026-06-06, 2026-06-07); 111 data rows (script).
- **reforge-history** — appended 2 days; 100 rows (script).
- **authorization-inventory** — regenerated: removed deleted `AdminMergeController`/`AdminDuplicateAccountsController`, added consolidated `UsersAdminAccountMergesController` (#899); moved per-human guards to `UsersAdminController` and debug routes to `DebugController` (#901); added `ShiftsController.Summary*` (#898) and new `UserController`; rewrote the `MembershipRequiredFilter` / stored-UserState name-only access story (#881).
- **controller-architecture-audit** — regenerated: dropped 2 deleted controllers, added `UserController` + `UsersAdminAccountMergesController`, moved the per-human admin surface onto `UsersAdminController`, replaced `ShiftsController` SignUp/SignUpRange with ToggleDay + added Summary/SummaryTeam/SummaryRota, relocated `CancelDeletion` to `UserController`.
- **dependency-graph** — regenerated Mermaid: moved `AccountMerge`/`DuplicateAccount` Profiles→Users (#899), dropped stale `DupAcct→Audit` & `Onboard→Metrics`, added `Consent→HumanLifecycle` (#881) and lazy `ShiftMgmt→Camp` (#898); recomputed linkStyle (261 eager / 18 lazy).
- **service-data-access-map** — regenerated: Profiles→Users moves; `DuplicateAccountService` is now detection-only (resolves the prior cross-section table-write violation block); added the `ShiftManagementService.BuildSummaryAsync` summary-by-camp read path (#898).
- **docs-readme-index** — fixed 2 drifted index descriptions (Membership Status Partition; Volunteer Status).

**Verified no-op / not dirty:** data-model-index (User.State/Shift additions are field-level, out of the entity-granularity index scope), guid-reservations (UserConfiguration change was a State enum conversion, no GUID literals), about-page-packages (no `Directory.Packages.props`/`.csproj` change), code-analysis-suppressions (no `Directory.Build.props`/`BannedSymbols.txt` change).

## Updated automatically (editorial drift-fix)

**Access / UserState (#881):**
- sections/Onboarding.md — corrected `HumanLifecycleService` surface (removed nonexistent `SetSuspendedAsync`/`SuspendForMissingConsentAsync`; now `ApplyProfileOnboardingMutationAsync` + `SuspendProfilesForMissingConsentAsync` + new `RestoreConsentSuspensionAsync`); removed deleted `ApproveVolunteerAsync` from director-write enumerations.
- features/onboarding/onboarding-pipeline.md — admission gate corrected to **name + consents**; app access granted at name entry (UserState==Active); `IsApproved`/Flag are audit-only; per-user sync replaced by batch `SystemTeamSyncJob`.
- guide/Onboarding.md — Volunteers-team provisioning follows name + consents (not coordinator clearance); a flag is an audit annotation that does not pause provisioning/access.
- features/shifts/coordinator-roles.md — coordinators do **not** bypass `MembershipRequiredFilter` (need UserState==Active); clearing/flagging are audit-only annotations.

**Account-merge consolidation (#899) + admin routes (#901):**
- sections/Users.md — added `User.State` (UserState) row; `AccountMergeService`/`AcceptAsync` Profiles→Users (IUserMerge fan-out); purge moved to `UsersAdminController` (`POST /Users/Admin/{id}/Purge`).
- features/global/administration.md — replaced deleted `AdminDuplicateAccountsController` + `AdminMergeController` sections with the unified `/Users/Admin/AccountMerges` surface; fixed Purge route; corrected the dashboard view-model record.
- guide/Admin.md — unified `/Users/Admin/AccountMerges` queue + `/Users/Admin/{id}/Purge`; updated freshness triggers.
- architecture/design-rules.md — §8 table-ownership map: moved `AccountMergeService`, `DuplicateAccountService`, and `account_merge_requests` Profiles→Users; reattributed the admin "merge" action to Users.
- sections/Profiles.md — Actors & Roles table updated: replaced retired `/Admin/DuplicateAccounts` + `/Admin/MergeRequests` with the unified `/Users/Admin/AccountMerges` (Users section).

**Profile routes (#901):**
- features/profiles/profiles.md — self-service routes moved under `/Profile/Me/*`; `CancelDeletion`→`/User/Deletion/Cancel`.
- features/profiles/preferred-email.md — `/Profile/Me/Emails`; dropped removed legacy redirect; admin email routes →`/Google/*`.

**Consent / jobs name-only switch (#881):**
- sections/LegalAndConsent.md — consent-submit no longer fires per-user team sync; CC clear/flag are audit annotations; `ConsentService` dropped `ISystemTeamSync`, added `IHumanLifecycleService.RestoreConsentSuspensionAsync`.
- guide/LegalAndConsent.md — Clear grants no access/team; Flag is non-final under name-only access.
- features/global/background-jobs.md — `SystemTeamSyncJob` single-user-sync note updated: consent-submit/CC-clear no longer call `SyncVolunteersMembershipForUserAsync`; admission reconciled by scheduled `SyncVolunteersTeamAsync`.
- features/governance/membership-status.md — corrected stale "Shared Logic" claim: `SystemTeamSyncJob` no longer consumes `PartitionUsersAsync` (computes Volunteers eligibility itself); partition now consumed only by `AdminDashboardService`. (Partition's 6-bucket framing itself verified unchanged.)

**Shift Summary by Camp (#898, additive):**
- sections/Shifts.md — added the Shift Summary by Camp concept + three `/Shifts/Summary` routes.
- features/shifts/shift-management.md — added the `/Shifts/Summary[/{teamSlug}[/{rotaGuid}]]` route row.

**Google integration (#881/#901):**
- sections/GoogleIntegration.md — Volunteers membership reconciled by scheduled `SystemTeamSyncJob`; consent-clear/flag no longer triggers per-user sync.
- features/google-integration/workspace-account-provisioning.md — human admin page →`/Users/Admin/{id}` (#901); `ProvisionEmail`→`/Google/Human/{id}/ProvisionEmail`.

## Pruned

- `docs/superpowers/plans/2026-05-05-email-problems-page.md` (2,327 lines) — all chaff (task list + code samples now in src); rationale retained in `docs/superpowers/specs/2026-05-05-email-problems-page-design.md`.
- `docs/superpowers/plans/2026-05-05-low-friction-shift-signup.md` (1,899 lines) — all chaff; the one net-new candidate (Public-rota force-Pending + `PromoteWidgetPendingSignupsAfterAdmissionAsync`) was verified **false** — that behavior was rejected; current code/`sections/Shifts.md` document the opposite, so it was dropped not migrated.

**Wheat migrated:** None — all durable design signal already lives in the retained design specs and section invariant docs.
**Prune total:** 4,226 lines = 5.0% of pre-prune (84,893) — at the soft target, under the 7% cap.
**Inbound refs:** only `docs/freshness/last-report.md` (this file, regenerated each run) — non-actionable.

## Review items — all resolved in Phase 7.5 (Peter approved fix-all)

**In-scope judgment calls — FIXED:**
1. **sections/admin-shell.md** — refreshed the stale sidebar-group list and per-role Actors & Roles cells to match current `AdminNavTree.cs` (Tickets, Members, Shifts, Barrios, Cantina, Expenses, Finance, Store, Event Guide, Governance, Google, Messaging, Agent, Legal, Audit, Diagnostics, Dev, Design, Temp); added a `freshness:triggers` block.
2. **architecture/design-rules.md §15i** (Teams migration log) — rewrote the stale prediction to past tense: `AccountMergeService` migrated to the **Users** section (`.Users`, not `.Profile` as projected); the lazy `IServiceProvider` `IEmailService` resolution in `TeamService` remains, so the cycle-break was not removed.
3. **sections/Profiles.md** — added one Invariants line: since #881, access is gated by stored `User.State` (UserState), superseding `Profile.State` (ProfileState) for access.

**Pre-existing out-of-scope drift (predated the `2f2ab285` anchor) — FIXED:**
4. sections/Onboarding.md — `IProfileService` → `IUserService` (OnboardingService's actual ctor dep / `ApplyProfileOnboardingMutationAsync`).
5. features/onboarding/volunteer-status.md — added `AdminSuspended` to the US-5.3 UserState filter list.
6. features/profiles/contact-fields.md — `/Profile/Emails` → `/Profile/Me/Emails` (both occurrences).
7. features/profiles/profile-pictures-birthdays.md — Picture route → `/Profile/Picture?id={id}` (query param); upload-size diagram 2MB → 20MB.
8. features/legal-and-consent/legal-documents-consent.md — corrected `SystemTeamIds` block (Volunteers 0001, Coordinators 0002, Board 0003, Asociados 0004, Colaboradors 0005, BarrioLeads 0006).
9. features/google-integration/drive-activity-monitoring.md — heading `/Admin/AuditLog` → `/AuditLog`.

**Unmarked editorial — `freshness:triggers` ADDED:**
- sections/admin-shell.md → `AdminNavTree.cs`, `AdminController.cs`, `_AdminLayout.cshtml`
- sections/Debug.md → `DebugController.cs`, `AdminNavTree.cs`

**Noted, no action:** authentication.md — User-entity sketch omits `State`, but the block is intentionally abbreviated (already omits many real columns); not a contradiction.

## Proposed for review

None — all prune-wheat candidates resolved this sweep (verified-true → already in living docs; verified-false → dropped).

## Questions

None pending. (One operational interruption occurred: a temp-dir cleanup tripped the machine's recursive-force-delete block; resolved with Peter inline, no doc impact.)

## Skipped (errors)

None.
