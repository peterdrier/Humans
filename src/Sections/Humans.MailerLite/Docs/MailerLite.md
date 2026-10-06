<!-- freshness:triggers
  src/Sections/Humans.MailerLite/**
  tests/Humans.MailerLite.Tests/**
  tests/Humans.Integration.Tests/Controllers/MailerLitePageRenderTests.cs
-->
<!-- freshness:flag-on-change
  Import classification/reset rules, "Humans - " group write guard, audience framework and universal Marketing opt-out exclusion, idempotency invariants, and admin routes. Review when MailerLite services, audiences, client, or architecture-test pins change.
-->

# MailerLite — Section Invariants

Orchestrates Humans ↔ MailerLite synchronisation. Inbound import + outbound audience management.

## Concepts

- **MailerLite subscriber** — a row in ML's `subscribers` collection. Has `status ∈ {active, unsubscribed, unconfirmed, bounced, junk}` and `subscribed_at` / `unsubscribed_at` / `opted_in_at` timestamps. Tracks the `groups` the subscriber belongs to.
- **Import plan** — the classified result of pulling the MailerLite `Website` group's subscribers and matching against Humans's user/email/preference state, plus a Humans-side pass that flags Marketing opt-ins the prior whole-account import wrongly set (see **Reset**). Built fresh on every preview/commit; never persisted between runs.
- **Apply** — executes an import plan: creates contacts, attaches verified users, deletes unverified UserEmail rows that block contact creation, updates Marketing preferences per the conflict rule, resets (deletes → null) Marketing flags caught by the reset pass, writes one summary audit.
- **Reset (marketing flag)** — a Humans-side cleanup decision: a Marketing opt-in written by the erroneous whole-account import (`UpdateSource = "MailerLiteSync"`, opted-in, no prior consent) on someone **not** in the `Website` group is deleted, reverting the category to "no preference" (null) — never to opt-out. GDPR remediation; cutoff for "prior consent" is hardcoded in `MailerLiteImportService.BadImportCutoff`.
- **Audience** — a code-defined `IMailerLiteAudience` implementation whose `MailerLiteGroupName` starts with `"Humans - "`. Membership is computed from Humans state and synced into the ML group by `MailerLiteAudienceSyncService` (opt-in Hangfire job, no default schedule — enabled by setting `MailerLite:AudienceSyncCron` — + on-demand admin button). All audiences derive from `MailerLiteAudienceBase`, which applies a universal Marketing opt-out exclusion (see Invariants).

## Data Model

MailerLite (the vendor) stays the system of record for subscriber state; Humans reads it via the API, and classifier writes route through other sections' services (`UserEmailService`, `AccountProvisioningService`, `CommunicationPreferenceService`).

The section owns one table, `mailerlite_sync_states` (`MailerLiteDbContext`, nobodies-collective/Humans#1082): **current** sync state, one row per key, overwritten on every run — never history.

| Column | Meaning |
|--------|---------|
| `Id` | Row identity; the `entityId` the run's audit entry points at, stable across runs. |
| `Key` | The `IMailerLiteAudience.Key`, or `import-reconciliation` for the import run. |
| `LastSyncAt`, `Summary` | When it last ran and the prose the dashboard renders. |
| `GroupId`, `GroupName`, and the seven counts | The audience push's outcome. Left at their defaults on the reconciliation row, which carries its numbers in `Summary`. |

## Routing

- `/MailerLite/Admin` — dashboard
- `/MailerLite/Admin/Import` — preview (GET)
- `/MailerLite/Admin/Import/Commit` — apply (POST)
- `/MailerLite/Admin/Audiences/{key}/Sync` — on-demand audience push (POST)
- `/MailerLite/Admin/SyncAll` — "Push All": push every audience in one action (POST)
- `/MailerLite/Admin/Refresh` — manual MailerLite cache refresh (POST)
- `/MailerLite/Admin/Audiences/{key}/Debug` — per-audience debug (GET) — five paged/sortable sections (expected, currently-in-ML, to-add, to-remove, non-primary diagnostic); Apply button posts to the existing `/Sync` action

All routes are `AdminOnly`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | none — section is admin-only |
| Admin | view dashboard, run import preview, commit import, push one audience, Push All, refresh the MailerLite cache, view the per-audience debug screen |

## Invariants

- Import commits reject binding errors with HTTP 400 and a Warning before rebuilding or applying a plan, so a malformed numeric limit cannot become an unlimited import. A valid blank limit still applies the whole plan; positive limits retain their existing per-outcome throttle.

- Shared table currency and number cells use the selected UI culture; numeric sort values stay invariant.

- Audience debug tables normalize the requested page and allowed page size once before slicing. Rows and pager state use the same available page, including the last page for oversized requests and page one for empty tables.

- `IMailerLiteService` exposes reads + five narrow outbound writes: `CreateGroupAsync`, `AssignSubscriberToGroupAsync`, `UnassignSubscriberFromGroupAsync`, `BulkImportSubscribersToGroupAsync`, and `DeleteSubscriberAsync` (GDPR Article 17 erasure, nobodies-collective/Humans#853). The set of allowed write methods is pinned by `MailerLiteArchitectureTests.IMailerLiteService_OnlyAllowsAudienceWrites`.
- The four audience-management writes target an ML group whose `Name` starts with `"Humans - "`; `MailerLiteClient` runtime-rejects those against non-`"Humans - "` groups with `InvalidOperationException` (pinned by `MailerLiteClientWriteGuardTests`). `DeleteSubscriberAsync` takes no group — erasure targets a subscriber, not a group, so the group guard does not apply.
- All `IMailerLiteAudience` implementations target group names starting with `"Humans - "`. Pinned by `MailerLiteArchitectureTests.AllAudiences_UseHumansPrefix`. Audience keys and group names are unique across registrations (pinned by `AllAudiences_HaveUniqueGroupNamesAndKeys`).
- Every audience excludes humans who have **explicitly opted out** of Marketing (`UserInfo.MarketingOptedOut == true`) — applied centrally in `MailerLiteAudienceBase.ComputeMemberUserIdsAsync` after the subclass computes its raw set, because MailerLite rejects opted-out addresses regardless of audience. Humans with no Marketing preference (null) or who opted in (false) are kept. For the two Marketing audiences this is subsumed by their stricter `== false` filter. Pinned by `MailerLiteAudienceBaseTests`.
- `MailerLiteImportService` and `MailerLiteAudienceSyncService` reach `mailerlite_sync_states` only through `IMailerLiteRepository`; nothing outside `Data/` holds a `MailerLiteDbContext`.
- Neither service reads `audit_log`. Sync state is read from the section's own table; audit descriptions are prose, never serialized JSON.
- No `IMailerLiteAudience` may claim the reserved `import-reconciliation` key (pinned by `MailerLiteArchitectureTests.AllAudiences_HaveUniqueGroupNamesAndKeys`).
- `mailerlite_sync_states` holds exactly one row per key. The repository's check-and-insert runs under a striped `TrackedLock` keyed on the sync key (the daily job and an admin's "Sync Audience" click can land together); there is no DB unique index. `ComputeAllStatsAsync` groups by key and takes the most recent rather than assuming uniqueness, so a duplicate shows the newest row instead of 500ing the dashboard (pinned by `ComputeAllStatsAsync_DuplicateKeyRows_ShowsTheNewest`).
- Import matching prefers a single verified owner. Multiple verified owners or multiple unverified matching email rows (including aliases on one owner) are skipped without deleting emails, provisioning a contact, or changing marketing preferences. Only a single unverified match is eligible for replacement.
- The import (`MailerLiteImportService`) ingests **only** the MailerLite group named `Website` (resolved by name; throws if the group is absent) — never the whole account. The reset pass excludes anyone in that group of any status, since the import already owns their pref (active → opt-in, unsubscribed/bounced → opt-out). Pinned by `MailerLiteImportServiceWebsiteScopeTests`.
- Every write to `CommunicationPreference[Marketing]` goes through `CommunicationPreferenceService` — `UpdatePreferenceAsync` for opt state, `ResetPreferenceAsync` to delete the row (→ null) — and produces a `CommunicationPreferenceChanged` audit entry on real state changes (not idempotent confirms).
- `ApplyAsync` is idempotent: a second run against unchanged ML+Humans state writes zero per-row entries and exactly one `MailerLiteReconciliationCompleted` summary entry.
- `SyncAsync` is idempotent: a second run against unchanged audience+ML state writes zero ML mutations and exactly one `MailerLiteAudienceSyncCompleted` summary entry whose counts are all zero.
- Bounced/junk subscribers always set `OptedOut = true` regardless of any Humans-side timestamp. Delivery facts override preferences.
- For non-bounce subscribers, Humans state wins only when the prior write's `UpdateSource ∈ {Profile, Guest, MagicLink, OneClick}` AND `UpdatedAt > mlActionAt`.
- `CommunicationPreference.SubscribedAt` is stamped on first known opt-in and never overwritten while non-null.
- Audience sync excludes ML subscribers with `status ∈ {unsubscribed, bounced, junk}` from group assignment — delivery/consent state overrides audience membership.
- `MailerLiteClient` retries a `429` response up to twice more (3 attempts total), honouring the response's `Retry-After` header (clamped to 0–90s; defaults to 60s when the header is absent or unparsable) before giving up (nobodies-collective/Humans#1103).
- Successful subscriber erasure (including a remote 404) removes the address from the cached subscriber list and recomputes account status totals from the remaining snapshot. A failed remote deletion retains both.
- Group creation requires a returned group before appending to the cached snapshot; missing/null response data fails without poisoning the existing group list.
- Cache refresh replaces the subscriber/group snapshot only after both page walks succeed. Missing page data or metadata, null subscriber/group items, repeated subscriber cursors, and inconsistent group page numbers throw and retain the last successful snapshot. Group reads follow pagination metadata even across empty intermediate pages.

## Negative Access Rules

- Non-admins **cannot** access any `/MailerLite/Admin/*` route.
- `IMailerLiteService` **cannot** be extended with write methods without removing the architecture-test pin in the same PR.
- `MailerLiteImportService` **cannot** inject `HumansDbContext` or any non-MailerLite repository (it goes through service interfaces).
- Code outside `CommunicationPreferenceService` **cannot** write to `communication_preferences` directly.

## Triggers

- When admin commits an import → one `MailerLiteReconciliationCompleted` audit entry with counts (no PII).
- When `ApplyAsync` flips `Marketing.OptedOut` → existing `CommunicationPreferenceChanged` audit fires through `CommunicationPreferenceService`.
- When `ApplyAsync` creates a contact → existing `ContactCreated` audit through `AccountProvisioningService`.
- When `MailerLiteAudienceSyncService.SyncAsync` runs (via the opt-in Hangfire job — `MailerLite:AudienceSyncCron`, unset by default — or the on-demand admin button) → one `MailerLiteAudienceSyncCompleted` audit entry with counts (no PII). Per-row ML mutations are not separately audited.

## Cross-Section Dependencies

The section references the contracts leaves `Humans.Users.Contracts`,
`Humans.Tickets.Contracts` and `Humans.Shifts.Contracts`, the section projects `Humans.Gdpr` and
`Humans.AuditLog` (contracts in their `Contracts/` folders), plus `Humans.Base`. There is no Profiles reference: the email
and communication-preference interfaces all live in `Humans.Users.Contracts`.

- **Users — people**: `IUserServiceRead.GetAllUserInfosAsync` and `GetUserInfoAsync` over the
  cached `UserInfo` set. `MailerLiteAudienceBase` reads it to drop explicit Marketing
  opt-outs from *every* audience; `MarketingAudience` / `MarketingNoTicketAudience` read it
  to enumerate explicit opt-ins (`UserInfo.MarketingOptedOut == false`); the debug screen
  reads it for names and addresses.
- **Users — email**: `IUserEmailService.GetNotificationTargetEmailsAsync` (the sync's
  user-id → address resolution), `GetPrimaryEmailAsync` and `GetVerifiedEmailsForUserAsync`
  (GDPR erasure), `FindByAddressAsync` (import matching: the verified-only pass counts
  distinct owners, the unverified pass picks the row to replace), `DeleteEmailAsync` (import remediation).
- **Users — preferences**: `ICommunicationPreferenceService.IsOptedOutAsync`,
  `GetPreferenceOrNullAsync`, `GetCountByCategoryAndStateAsync` (reads) and
  `UpdatePreferenceAsync`, `ResetPreferenceAsync` (writes, from the import apply).
- **Users — provisioning**: `IAccountProvisioningService.FindOrCreateUserByEmailAsync`, the
  import's create path.
- **Tickets**: `ITicketServiceRead.GetTicketOrdersAsync`, called once from
  `CurrentEventTicketHolders.ForCurrentEventAsync` — the single definition of the
  current-event ticket-holder set that `HasTicketAudience`, `MarketingNoTicketAudience` and
  `TicketNoShiftsAudience` all read.
- **Shifts**: `IShiftView.GetUsersAsync` — cached per-user shift signups. `HasShiftAudience`
  and the per-period audiences share `ShiftViewAudienceBase`, differing only in the
  predicate they apply to `ShiftUserSummary` (`HasShift`, or `HasShiftInPeriod` for Build /
  Event / Strike; Setup = Build). `TicketNoShiftsAudience` reads the same view directly.
- **AuditLog**: writes via `IAuditLogService.LogAsync` (job overload).
- **GDPR**: `MailerLiteGdprContributor` implements `IUserDataContributor` — Article 15 export contributes nothing (the subscriber list mirrors state already exported by the sections that generate it); Article 17 erasure deletes the MailerLite subscriber under every verified + primary email via `IMailerLiteService.DeleteSubscriberAsync`.

## Architecture

**Owning services:** `MailerLiteImportService`, `MailerLiteAudienceSyncService`, `MailerLiteClient`, `MailerLiteGdprContributor`
**Owned tables:** `mailerlite_sync_states` (`MailerLiteDbContext` / `IMailerLiteRepository`)
**Status:** Own project — `src/Sections/Humans.MailerLite` (nobodies-collective/Humans#866).

- Everything lives in `src/Sections/Humans.MailerLite/`: `Services/` (the orchestrators, the service interfaces and the internal DTOs), `Services/Audiences/`, `Services/MailerLite/` (the client, its JSON converter and `MailerLiteOptions`), `Domain/`, `Data/` (context, design-time factory, configuration, repository, migrations), `Controllers/`, `Models/` and `Views/`. The section still takes **no `Humans.Infrastructure` reference** — the context registers through Base's `AddSectionDbContext` seam, and the client needs only `IHttpClientFactory` from the ASP.NET shared framework.
- **Cross-section surface** — none. The section is a leaf: `MailerLiteAudienceSyncJob` (`Jobs/`) is internal and calls `IMailerLiteAudienceSyncService.SyncAllAsync` as the actor-less scheduled run, contributed through `SectionJobs` like every other section's jobs. The dashboard stats, the import plan/apply pair and the whole `IMailerLiteService` surface are internal too.
- **No resource set** — the admin pages are English operator copy with no `Localizer[…]` call; admin-only pages are exempt from localization ([`localization-admin-exempt`](../../../../memory/code/localization-admin-exempt.md)).
- **Decorator decision** — no caching decorator. Rationale: admin-only, sequential, runs by hand; one DB count per dashboard load is fine at our scale. `MailerLiteClient` is a Singleton holding its own subscriber/group snapshot, refreshed only on demand.
- **Cross-section calls** — `IUserEmailService`, `IAccountProvisioningService`, `ICommunicationPreferenceService`, `IUserServiceRead`, `ITicketServiceRead`, `IShiftView`, `IAuditLogService`.
- **Architecture test** — `tests/Humans.MailerLite.Tests/Architecture/MailerLiteArchitectureTests.cs` pins: namespace, allowed-write surface on `IMailerLiteService`, audience group-name prefix + uniqueness, and (`EveryController_IsAdminOnly`) the admin-only policy — the pin CI runs. `MailerLiteClientWriteGuardTests` pins the runtime "Humans - " prefix guard; `MailerLitePageRenderTests` (in `Humans.Integration.Tests`, which CI does not run) checks that the section's own `_ViewImports` binds and that `/MailerLite/Admin/*` stays admin-only.
