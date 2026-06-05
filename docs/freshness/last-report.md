# Freshness Sweep Report

**Run:** 2026-06-04 21:34 UTC
**Mode:** diff (batch)
**Diff anchor:** `37dc784f` → `17808b4c` (upstream/main HEAD)
**Worktree base:** `origin/main` `6af1ed1b` — ⚠️ advanced to `da245fbca` (PR #887, Store Summary view+CSS only) mid-sweep; see *Moved base* below.
**Entries updated:** 16 (8 mechanical + 8 editorial) + post-review fixes · **Pruned:** 10 husks (~19,968 lines) · **Errors:** 0

> **Post-review (2026-06-05):** Peter reviewed the items below inline and directed action. Applied this PR: guide one-liner for the burner-name collision warning; refreshed design-rules §15 (dead `CachingProfileService`/`FullProfile` refs → `CachingUserService`/`UserInfo`; decorator count 1→11); corrected the Store treasury-sync + US-30.4 Phase-5 claims; a full prune of the deferred shipped-feature plans (9 more husks); plus a `Profiles.md` account-merge fan-out drift fix (`IUserMerge`) surfaced during the prune.

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

**10 husks deleted (~19,968 lines)** — each shipped + >30 days old (or feature removed this window), with every durable rule verified to already live in the section/feature docs (or in source). Each had its inbound refs checked.

| Husk | Lines | Why safe |
|---|---|---|
| `plans/2026-05-04-shift-range-signup-confirmation.md` | 1,437 | Feature **removed this window** (per-day `ToggleDay`); rules already in `Shifts.md`/`shift-management.md`. |
| `superpowers/plans/2026-04-29-issues-section.md` | 2,493 | Shipped; rules in `sections/Issues.md` + `features/issues/issues-system.md` (plan now even stale: missing Scanner section, Camps→Barrios). |
| `superpowers/plans/2026-04-30-store-section.md` | 1,822 | Shipped; rules in `sections/Store.md` + `features/store/store.md`. Inbound ref at `Store.md:265` retargeted (dropped plan path, kept the design spec). |
| `superpowers/plans/2026-05-04-ticket-transfer.md` | 2,505 | Vendor void+reissue engine **removed before ship**; manual model fully in `Tickets.md`. |
| `superpowers/plans/2026-05-01-account-merge-fold-redesign.md` | 960 | Per-interface fan-out **superseded by `IUserMerge`**; one durable gotcha migrated (below). |
| `superpowers/plans/2026-04-21-community-calendar-slice1.md` | 2,670 | Shipped; rules in `Calendar.md`/`community-calendar.md` (which post-date the plan). Lone ref is a dated `maintenance-log.md` line — left as a historical record (log is hand-maintained). |
| `superpowers/plans/2026-04-21-in-app-guide.md` | 2,866 | Shipped; rules in `Guide.md`/`in-app-guide.md`. No inbound refs. |
| `superpowers/plans/2026-04-26-holded-read-integration.md` | 3,056 | Core slug-tag scheme declared **dead-on-arrival** in a later spec (Holded strips tag separators); shipped design in `Finance.md`/`Holded.md`. |
| `superpowers/plans/2026-04-26-admin-shell-left-nav.md` | 1,985 | Shipped; in `admin-shell.md`; the one rationale (traffic-ordered nav) lives in `AdminNavTree.cs:6-8`. |
| `plans/2026-05-03-event-guide-route-renaming.md` | 174 | Routes shipped + pinned by an arch test; rationale in `Events.md`. |

- **Wheat migrated (1):** `account-merge-fold-redesign` → `design-rules.md §12` — the merge chain-follow on append-only reads, the **cache-only** `GetMergedSourceIdsAsync` (inner throws `NotSupportedException` — `UserService.cs:1288`), and the **16-hop transitive cap** guarding circular merges (`GoogleWorkspaceSyncService.cs:295`). Everything else across the 10 husks was already present or chaff.
- **Surfaced + fixed during prune:** `Profiles.md` ~399–410 still described the superseded 16-interface `Reassign…ToUserAsync` fan-out — corrected to the shipped `IUserMerge.ReassignAsync` fan-out (and adjacent dead `CachingProfileService`/`FullProfile` → `CachingUserService`/`UserInfo`).
- **Intentionally retained:** `2026-04-25-freshness-sweep.md` (meta — this skill's own design history, and the skill was being edited concurrently); `tech-debt-2026-04-23.md` (no `[DONE]` markers, all-DONE condition unverifiable); `2026-03-15-ticket-vendor-integration-design.md` (the rationale home a prior sweep deliberately kept).

## Flagged for human review

All flagged items resolved at Peter's direction (see the post-review note up top): §15 / Store-treasury / US-30.4 fixed; `sections/Teams.md` L210 — the `IsSensitive` invariant now states explicitly that only a global Admin can set/clear it (it's an auth rule, so it's documented); camp-members alphabetization — intentionally **not** documented (default sort behavior, not invariant-worthy). None outstanding.

## Proposed for review

None — all prune candidates resolved this sweep (the prune subagent returned no uncertain items).

## Questions — resolved inline (2026-06-05)

- Full Stripe-reconciliation user story in `store.md`? → **No** — the brief reconciliation note stands.
- End-user-guide mention of the burner-name collision warning? → **Yes** — one-liner added to `guide/Profiles.md`.

## Moved base

`origin/main` fast-forwarded `6af1ed1b3` → `da245fbca` (PR #887, "store summary totals row…") while the sweep ran. #887 touches only `Views/StoreAdmin/Summary.cshtml` + `wwwroot/css/admin-shell.css` — **no** interface/service/repository/controller-action/entity/enum/auth surface, and **not** in `upstream/main` (the diff anchor), so it is outside this sweep's window and the next sweep will cover it. No regenerated doc references anything it changed; the surface report compares merge-base (`6af1ed1b`)..head, so no merge/re-run was needed and the PR remains a clean docs-only diff.

**Next-sweep note:** #887 drops the cross-tab grand total from the Store Summary page. The pre-existing cross-tab descriptions in `features/store/store.md` (US "Cross-tab … grand total"), `sections/Store.md`, and `guide/Store.md` still reflect the pre-#887 layout (correct for this sweep's anchor) and should be reconciled by the next sweep once #887 reaches `upstream/main`.

## Skipped (errors)

None.
