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

MailerLite (the vendor) stays the system of record for subscriber state; Humans reads it via the API, and classifier writes route through other sections' services (`UserEmailService`, `AccountProvisioningService`, `CommunicationPreferenceService`, `UserService`).

The section owns one table, `mailerlite_sync_states` (`MailerLiteDbContext`, nobodies-collective/Humans#1082): **current** sync state, one row per key, overwritten on every run — never history.

| Column | Meaning |
|--------|---------|
| `Id` | Row identity; the `entityId` the run's audit entry points at, stable across runs. |
| `Key` | The `IMailerLiteAudience.Key`, or `import-reconciliation` for the import run. |
| `LastSyncAt`, `Summary` | When it last ran and the prose the dashboard renders. |
| `GroupId`, `GroupName`, and the seven counts | The audience push's outcome. Left at their defaults on the reconciliation row, which carries its numbers in `Summary`. |

Before #1082 this state was `JsonSerializer.Serialize`d into an `audit_log` `Description` with `entityId: Guid.Empty` and parsed back out on read. Those historical rows stay where they are; the table starts empty and fills on the next sync.

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
| Admin | view dashboard, run import preview, commit import |

## Invariants

- `IMailerLiteService` exposes reads + five narrow outbound writes: `CreateGroupAsync`, `AssignSubscriberToGroupAsync`, `UnassignSubscriberFromGroupAsync`, `BulkImportSubscribersToGroupAsync`, and `DeleteSubscriberAsync` (GDPR Article 17 erasure, nobodies-collective/Humans#853). The set of allowed write methods is pinned by `MailerLiteArchitectureTests.IMailerLiteService_OnlyAllowsAudienceWrites`.
- The four audience-management writes target an ML group whose `Name` starts with `"Humans - "`; `MailerLiteClient` runtime-rejects those against non-`"Humans - "` groups with `InvalidOperationException` (pinned by `MailerLiteClientWriteGuardTests`). `DeleteSubscriberAsync` is exempt — erasure removes the person outright regardless of group membership.
- All `IMailerLiteAudience` implementations target group names starting with `"Humans - "`. Pinned by `MailerLiteArchitectureTests.AllAudiences_UseHumansPrefix`. Audience keys and group names are unique across registrations (pinned by `AllAudiences_HaveUniqueGroupNamesAndKeys`).
- Every audience excludes humans who have **explicitly opted out** of Marketing (`UserInfo.MarketingOptedOut == true`) — applied centrally in `MailerLiteAudienceBase.ComputeMemberUserIdsAsync` after the subclass computes its raw set, because MailerLite rejects opted-out addresses regardless of audience. Humans with no Marketing preference (null) or who opted in (false) are kept. For the two Marketing audiences this is subsumed by their stricter `== false` filter. Pinned by `MailerLiteAudienceBaseTests`.
- `MailerLiteImportService` and `MailerLiteAudienceSyncService` reach `mailerlite_sync_states` only through `IMailerLiteRepository`; nothing outside `Data/` holds a `MailerLiteDbContext`.
- Neither service reads `audit_log`. Sync state is read from the section's own table; audit descriptions are prose, never serialized JSON.
- No `IMailerLiteAudience` may claim the reserved `import-reconciliation` key.
- `mailerlite_sync_states` holds exactly one row per key. The repository's check-and-insert runs under a striped `TrackedLock` keyed on the sync key (the daily job and an admin's "Sync Audience" click can land together); there is no DB unique index. `ComputeAllStatsAsync` groups by key and takes the most recent rather than assuming uniqueness, so a duplicate would show a stale number instead of 500ing the dashboard.
- The import (`MailerLiteImportService`) ingests **only** the MailerLite group named `Website` (resolved by name; throws if the group is absent) — never the whole account. The reset pass excludes anyone in that group of any status, since the import already owns their pref (active → opt-in, unsubscribed/bounced → opt-out). Pinned by `MailerLiteImportServiceWebsiteScopeTests`.
- Every write to `CommunicationPreference[Marketing]` goes through `CommunicationPreferenceService` — `UpdatePreferenceAsync` for opt state, `ResetPreferenceAsync` to delete the row (→ null) — and produces a `CommunicationPreferenceChanged` audit entry on real state changes (not idempotent confirms).
- `ApplyAsync` is idempotent: a second run against unchanged ML+Humans state writes zero per-row entries and exactly one `MailerLiteReconciliationCompleted` summary entry.
- `SyncAsync` is idempotent: a second run against unchanged audience+ML state writes zero ML mutations and exactly one `MailerLiteAudienceSyncCompleted` summary entry whose counts are all zero.
- Bounced/junk subscribers always set `OptedOut = true` regardless of any Humans-side timestamp. Delivery facts override preferences.
- For non-bounce subscribers, Humans state wins only when the prior write's `UpdateSource ∈ {Profile, Guest, MagicLink, OneClick}` AND `UpdatedAt > mlActionAt`.
- `CommunicationPreference.SubscribedAt` is stamped on first known opt-in and never overwritten while non-null.
- Audience sync excludes ML subscribers with `status ∈ {unsubscribed, bounced, junk}` from group assignment — delivery/consent state overrides audience membership.
- `MailerLiteClient` retries a `429` response up to twice more (3 attempts total), honouring the response's `Retry-After` header (clamped to 0–90s; defaults to 60s when the header is absent or unparsable) before giving up (nobodies-collective/Humans#1103).

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

The section references these contracts leaves — `Humans.Users.Contracts`,
`Humans.Tickets.Contracts`, `Humans.Shifts.Contracts`, `Humans.Gdpr.Contracts`,
`Humans.AuditLog.Contracts` — plus `Humans.Base`. There is no Profiles reference: the email
and communication-preference interfaces all live in `Humans.Users.Contracts`.

- **Users — people**: `IUserServiceRead.GetAllUserInfosAsync` and `GetUserInfoAsync` over the
  cached `UserInfo` set. `MailerLiteAudienceBase` reads it to drop explicit Marketing
  opt-outs from *every* audience; `MarketingAudience` / `MarketingNoTicketAudience` read it
  to enumerate explicit opt-ins (`UserInfo.MarketingOptedOut == false`); the debug screen
  reads it for names and addresses.
- **Users — email**: `IUserEmailService.GetNotificationTargetEmailsAsync` (the sync's
  user-id → address resolution), `GetPrimaryEmailAsync` and `GetVerifiedEmailsForUserAsync`
  (GDPR erasure), `FindAnyEmailRowByAddressAsync` and `GetDistinctVerifiedUserIdsAsync`
  (import matching), `DeleteEmailAsync` (import remediation).
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
**Status:** (G5) Own project — `src/Sections/Humans.MailerLite`, moved 2026-08-11 (nobodies-collective/Humans#866); the `Humans.MailerLite.Contracts` leaf later folded into the project's `Contracts/` folder. Born §15-compliant on 2026-05-12; outbound + audience framework added 2026-05-14.

- Everything lives in `src/Sections/Humans.MailerLite/`: `Services/` (the orchestrators, the service interfaces and the internal DTOs), `Services/Audiences/`, `Services/MailerLite/` (the client, its JSON converters and `MailerLiteOptions`), `Domain/`, `Data/` (context, design-time factory, configuration, repository, migrations), `Controllers/`, `Models/` and `Views/`. The section still takes **no `Humans.Infrastructure` reference** — the context registers through Base's `AddSectionDbContext` seam, and the client needs only `IHttpClientFactory` from the ASP.NET shared framework.
- **Cross-section surface** — `IMailerLiteAudienceSync` (`Contracts/`): `SyncAllAudiencesAsync`, returning `int`. `MailerLiteAudienceSyncJob` — its only consumer — moved into this project's `Jobs/` folder at G5 lane 5b-5 (public, since Shell names the concrete type at registration and HUM0034 makes every other public type in a section an error), leaving only its DI registration and roll-call entry in Shell (no `ISection` seam for jobs yet, design §15 step 6b). With no Base consumer left, the `Humans.MailerLite.Contracts` leaf was no longer forced and folded into this project's `Contracts/` folder beside the interface. Nothing else — the dashboard stats, the import plan/apply pair and the whole `IMailerLiteService` surface are internal.
- **No resource set** — the admin pages are English operator copy with no `Localizer[…]` call. `MailerLiteArchitectureTests.SectionTypesTakeNoStringLocalizer` is what makes adding copy fail the build rather than silently resolving against a `SharedResource` the RCL cannot see.
- **Decorator decision** — no caching decorator. Rationale: admin-only, sequential, runs by hand; one DB count per dashboard load is fine at 500 users. `MailerLiteClient` is a Singleton holding its own subscriber/group snapshot, refreshed only on demand.
- **Cross-section calls** — `IUserEmailService`, `IAccountProvisioningService`, `ICommunicationPreferenceService`, `IUserServiceRead`, `ITicketServiceRead`, `IShiftView`, `IAuditLogService`.
- **Architecture test** — `tests/Humans.MailerLite.Tests/Architecture/MailerLiteArchitectureTests.cs` pins: namespace, no `IStringLocalizer<T>` anywhere in the section, allowed-write surface on `IMailerLiteService`, and audience group-name prefix + uniqueness. `MailerLiteClientWriteGuardTests` pins the runtime "Humans - " prefix guard; `MailerLitePageRenderTests` (in `Humans.Integration.Tests`) pins that the section's own `_ViewImports` binds and that `/MailerLite/Admin/*` stays admin-only.
