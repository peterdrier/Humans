# Freshness Sweep Report

**Run:** 2026-06-04 21:34 UTC
**Mode:** diff (batch)
**Diff anchor:** `37dc784f` → `17808b4c` (upstream/main HEAD)
**Worktree base:** `origin/main` `6af1ed1b` — ⚠️ advanced to `da245fbca` (PR #887, Store Summary view+CSS only) mid-sweep; see *Moved base* below.
**Entries updated:** 16 (8 mechanical + 8 editorial) · **Pruned:** 1 husk (1,437 lines) · **Errors:** 0

The diff window spans: Stripe payment reconciliation + recorded-payments + open-order repricing (Store), per-day instant shift signup replacing the range picker (Shifts), the onboarding burner/legal-name gate (`NameRequiredFilter`), the live burner-name collision warning + pure search matcher (Profiles), camp members UI + `ICampServiceRead.GetCampUserInfoAsync` (Camps), `IsSensitive` leave-unchanged on non-Admin team edit (Teams), and in-memory verified-email matching for Who-Hasn't-Bought (Tickets).

## Updated automatically

### Mechanical
- `dev-stats` — appended the 2026-06-04 row to the Codebase Growth table (reforge-sourced class/interface counts).
- `reforge-history` — appended the 2026-06-04 snapshot row (97 rows / 97 distinct days).
- `about-page-packages` — bumped `Google.Apis.Admin.Directory.directory_v1` 1.74.0.4128 → 1.74.0.4159; all other cards already matched the manifest.
- `docs-readme-index` — added 2 missing feature rows (`profiles/burner-name-collision-warning.md`, `user-search-overhaul.md`); sections/guide tables already complete.
- `authorization-inventory` — added `ProfileApiController.BurnerNameCount`, `ShiftsController.ToggleDay`, `StoreAdminController` Payments/RecordMissingPayments, and the global `NameRequiredFilter`.
- `controller-architecture-audit` — added the three new actions, removed deleted `TicketTransferAdmin.RetryIssue`; all 80 controllers re-verified.
- `dependency-graph` — regenerated the Mermaid body and reconciled the fan-in prose: corrected 9 mislabeled notification edges (`INotificationEmitter` injectors were drawn into the `NotificationService` node); `NotificationEmitter` is the high-fan-in node (15 dependents), `NotificationService` has a single dependent (`AccountMergeService`). *(Pre-existing drift, fixed by full regen.)*
- `service-data-access-map` — refreshed Profiles (`PersonSearchMatcher`), Tickets (in-memory verified-email match; removed `SearchUserIdsByVerifiedEmailAsync`; `ICampaignServiceRead` read-split), Store (reconciliation + repricing surfaces); verified no cross-section table-read violations introduced.

`data-model-index` and `guid-reservations` were dirty by trigger but regenerated identically (StoreOrder change was field-level, no new entity; `StoreOrderConfiguration` carries no deterministic GUID literals). `code-analysis-suppressions` was not dirty (no change to `Directory.Build.props` / `BannedSymbols.txt`).

### Editorial (drift-fix)
- `Store` (`sections/Store.md`, `features/store/store.md`, `guide/Store.md`) — order Label removed from UI; Open orders reprice to live catalog price (snapshot frozen only at InvoiceIssued); new `StoreProductPriceChanged` audit + order-page price-changes table; added the Stripe Payments reconciliation admin page to the guide.
- `Shifts` (`sections/Shifts.md`, `guide/Shifts.md`, `features/shifts/shift-management.md`) — replaced the removed `SignUp`/`SignUpRange` browse-page routes with `POST /Shifts/ToggleDay` (per-day instant toggle, no reload/confirm; overlap = block+warn). Range signup retained only for the onboarding widget.
- `Auth/Onboarding` (`features/auth/authentication.md`, `sections/Onboarding.md`, `features/onboarding/onboarding-pipeline.md`, `guide/Onboarding.md`) — documented the global `NameRequiredFilter` name-gate (nameless authenticated users forced to `OnboardingWidget/Names`, never blocks sign-in).
- `Camps` (`sections/Camps.md`, `features/camps/camps.md`) — added `GetCampUserInfoAsync` to the `ICampServiceRead` surface; updated the Camp Members role-slot UI (collapsed descriptions + avatar-pill slots).
- `Profiles` (`sections/Profiles.md`) — added the admin-only camp name + roles block now rendered in the authenticated `_HumanPopover`.
- `Teams` (`sections/Teams.md`) — only a global Admin can change `IsSensitive` via Edit Team; a non-Admin's save leaves it unchanged (ref #824).
- `AuditLog` (`sections/AuditLog.md`) — added `StoreProductPriceChanged` and `StorePaymentsReconciled` to the enumerated Store audit actions.
- `design-rules.md` — §15f wrongly listed `PersonSearchMatcher` among "old names that no longer exist"; PR #869 reintroduced it as a real class — removed from the list.

### Reviewed, no drift
Tickets (`sections/Tickets.md` already current; behavioral docs unchanged), gdpr-export / email-flag-violations-remediation / `guide/Admin.md` (the changed surfaces aren't enumerated at the altitude these docs describe), `conventions.md` / `code-review-rules.md` / `coding-rules.md` (no rule contradicted), `burner-name-collision-warning.md` / `profile-search-detail.md` / `profiles.md` / `guide/Profiles.md` / `profile-pictures-birthdays.md` / `public-coordinator-popover.md` (the public popover is anonymous and must *not* show camp info), `membership-tiers.md`, and the eight docs triggered solely by ProfileController's 6-line change (whose only effect is the popover camp-info: `communication-preferences`, `contact-fields`, `dietary-medical-nudge`, `preferred-email`, `coordinator-roles`, `administration`, `guide/Email`, `workspace-account-provisioning`). `sections/Guide.md` (no guide page added/removed) and `google-removal-notifications.md` (the changed resx keys are burner-name + shift-toggle strings, unrelated) — over-broad triggers, no drift.

## Pruned

- **`docs/superpowers/plans/2026-05-04-shift-range-signup-confirmation.md`** — deleted (1,437 lines). The browse-page range-picker + confirmation-modal flow it designed was **removed this window** (replaced by `POST /Shifts/ToggleDay`). All durable rules it touches are already captured in the living docs (overlap rule + event-tz absolute instants, 08:00–18:00 all-day window, `SignupBlockId` atomic range blocks, conflicts-as-warnings, the EE "arrive by start−1 day" grant, policy-driven signup status — all in `Shifts.md`; the cut-over itself in `shift-management.md` US-25.3). Its own `bool skipConflicts` API shape is additionally stale vs current `ShiftSignupRequestFlags.SkipConflicts`. Remaining content is implementation scaffolding for a deleted flow. No inbound doc refs.
- **Wheat migrated:** none — every durable item was already present in the target docs (verified against `ShiftSignupService`, `Shift.AllDayWindowStart/End`, `ShiftsController.ToggleDay`).

**Deferred prune candidates (budget decision, not a punt):** the larger >30-day shipped-feature plans (`2026-04-29-issues-section.md`, `2026-04-30-store-section.md`, `2026-04-26-holded-read-integration.md`, `2026-04-21-{community-calendar-slice1,in-app-guide}.md`, etc.) were not analyzed this sweep. They describe **still-live** features and likely carry ADR-level vendor/decorator rationale that needs careful per-item wheat-extraction — deferred to keep this docs PR reviewable rather than rush delicate extraction. `tech-debt-2026-04-23.md` uses no `[DONE]` markers, so the all-DONE prune condition can't be verified — left in place. The `2026-03-15-ticket-vendor-integration-design.md` spec is the rationale home a prior sweep deliberately retained — left in place.

## Flagged for human review

These are subjective/pre-existing items where **no code fact from this window is contradicted** — recorded, not edited:

- `design-rules.md` §15i — the dated 2026-04-23 "Known Current Violations" snapshot still calls `CachingProfileService` the live caching decorator, but the canonical example and source are now `CachingUserService`/`TrackedCache<Guid,UserInfo>` (`CachingProfileService`/`FullProfile` no longer exist). Pre-existing snapshot staleness; the whole §15i block may want a dedicated refresh.
- `guide/Store.md` — bank-transfer prose says transfers "are matched … and applied automatically when the next sync runs," but the treasury sync job is Phase 7 / not yet implemented. Pre-existing, outside this window's triggers.
- `features/store/store.md` — US-30.4 (Record a Manual Payment) carries no "paused" note even though `RecordManualPaymentAsync` throws `NotSupportedException("Phase 5")`. Pre-existing.
- `sections/Teams.md` L210 — the `IsSensitive` invariant doesn't explicitly state *who* may change it (now carried in the actors table); a maintainer may want an explicit clause.
- `sections/Camps.md` / `features/camps/camps.md` — camp-members alphabetization and `ICampInfoInvalidator.InvalidateAllAsync` are new but contradict no existing sentence, so not added; flag if the new ordering / all-projection invalidation should be documented.

## Proposed for review

None — all prune candidates resolved this sweep (the prune subagent returned no uncertain items).

## Questions (editorial scope — author-new-content judgment calls, Peter's call)

- `features/store/store.md` / `guide/Store.md`: the sweep added a brief reconciliation note where load-bearing but did **not** author a full new "Stripe payment reconciliation" user story (e.g. US-30.8). Author one?
- `guide/Profiles.md` / `profile-search-detail.md`: the new live burner-name collision warning on Edit Profile is user-facing but absent from the end-user guide (which already omits the pre-existing legal-name-match warning). Add a one-line mention?

## Moved base

`origin/main` fast-forwarded `6af1ed1b3` → `da245fbca` (PR #887, "store summary totals row…") while the sweep ran. #887 touches only `Views/StoreAdmin/Summary.cshtml` + `wwwroot/css/admin-shell.css` — **no** interface/service/repository/controller-action/entity/enum/auth surface, and **not** in `upstream/main` (the diff anchor), so it is outside this sweep's window and the next sweep will cover it. No regenerated doc references anything it changed; the surface report compares merge-base (`6af1ed1b`)..head, so no merge/re-run was needed and the PR remains a clean docs-only diff.

**Next-sweep note:** #887 drops the cross-tab grand total from the Store Summary page. The pre-existing cross-tab descriptions in `features/store/store.md` (US "Cross-tab … grand total"), `sections/Store.md`, and `guide/Store.md` still reflect the pre-#887 layout (correct for this sweep's anchor) and should be reconciled by the next sweep once #887 reaches `upstream/main`.

## Skipped (errors)

None.
