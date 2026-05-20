# Mailer — Section Invariants

Orchestrates Humans ↔ MailerLite synchronisation. Inbound import + outbound audience management.

## Concepts

- **MailerLite subscriber** — a row in ML's `subscribers` collection. Has `status ∈ {active, unsubscribed, unconfirmed, bounced, junk}` and `subscribed_at` / `unsubscribed_at` / `opted_in_at` timestamps. Tracks the `groups` the subscriber belongs to.
- **Import plan** — the classified result of pulling every ML subscriber and matching against Humans's user/email/preference state. Built fresh on every preview/commit; never persisted between runs.
- **Apply** — executes an import plan: creates contacts, attaches verified users, deletes unverified UserEmail rows that block contact creation, updates Marketing preferences per the conflict rule, writes one summary audit.
- **Audience** — a code-defined `IMailerAudience` implementation whose `MailerLiteGroupName` starts with `"Humans - "`. Membership is computed from Humans state and synced into the ML group by `MailerAudienceSyncService` (daily Hangfire job + on-demand admin button).

## Data Model

Mailer owns no tables. MailerLite is the system of record for subscriber state; Humans reads via the API. Classifier writes route through other sections' services (`UserEmailService`, `AccountProvisioningService`, `CommunicationPreferenceService`, `UserService`).

## Routing

- `/Mailer/Admin` — dashboard
- `/Mailer/Admin/Import` — preview (GET)
- `/Mailer/Admin/Import/Commit` — apply (POST)
- `/Mailer/Admin/Audiences/{key}/Sync` — on-demand audience push (POST)
- `/Mailer/Admin/Audiences/{key}/Debug` — per-audience debug (GET) — five paged/sortable sections (expected, currently-in-ML, to-add, to-remove, non-primary diagnostic); Apply button posts to the existing `/Sync` action

All routes are `AdminOnly`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | none — section is admin-only |
| Admin | view dashboard, run import preview, commit import |

## Invariants

- `IMailerLiteService` exposes reads + four narrow outbound writes: `CreateGroupAsync`, `AssignSubscriberToGroupAsync`, `UnassignSubscriberFromGroupAsync`, `BulkImportSubscribersToGroupAsync`. The set of allowed write methods is pinned by `MailerArchitectureTests.IMailerLiteService_OnlyAllowsAudienceWrites`.
- Every outbound write targets an ML group whose `Name` starts with `"Humans - "`. `MailerLiteClient` runtime-rejects writes against non-`"Humans - "` groups with `InvalidOperationException`. Pinned by `MailerLiteClientWriteGuardTests`.
- All `IMailerAudience` implementations target group names starting with `"Humans - "`. Pinned by `MailerArchitectureTests.AllAudiences_UseHumansPrefix`. Audience keys and group names are unique across registrations (pinned by `AllAudiences_HaveUniqueGroupNamesAndKeys`).
- `MailerImportService` and `MailerAudienceSyncService` live in `Humans.Application` and never reference `Microsoft.EntityFrameworkCore`. Pinned by architecture tests.
- Every write to `CommunicationPreference[Marketing]` goes through `CommunicationPreferenceService.UpdatePreferenceAsync` and produces a `CommunicationPreferenceChanged` audit entry on real state changes (not idempotent confirms).
- `ApplyAsync` is idempotent: a second run against unchanged ML+Humans state writes zero per-row entries and exactly one `MailerLiteReconciliationCompleted` summary entry.
- `SyncAsync` is idempotent: a second run against unchanged audience+ML state writes zero ML mutations and exactly one `MailerLiteAudienceSyncCompleted` summary entry whose counts are all zero.
- Bounced/junk subscribers always set `OptedOut = true` regardless of any Humans-side timestamp. Delivery facts override preferences.
- For non-bounce subscribers, Humans state wins only when the prior write's `UpdateSource ∈ {Profile, Guest, MagicLink, OneClick}` AND `UpdatedAt > mlActionAt`.
- `CommunicationPreference.SubscribedAt` is stamped on first known opt-in and never overwritten while non-null.
- Audience sync excludes ML subscribers with `status ∈ {unsubscribed, bounced, junk}` from group assignment — delivery/consent state overrides audience membership.

## Negative Access Rules

- Non-admins **cannot** access any `/Mailer/Admin/*` route.
- `IMailerLiteService` **cannot** be extended with write methods without removing the architecture-test pin in the same PR.
- `MailerImportService` **cannot** inject `HumansDbContext` or any non-Mailer repository (it goes through service interfaces).
- Code outside `CommunicationPreferenceService` **cannot** write to `communication_preferences` directly.

## Triggers

- When admin commits an import → one `MailerLiteReconciliationCompleted` audit entry with counts (no PII).
- When `ApplyAsync` flips `Marketing.OptedOut` → existing `CommunicationPreferenceChanged` audit fires through `CommunicationPreferenceService`.
- When `ApplyAsync` creates a contact → existing `ContactCreated` audit through `AccountProvisioningService`.
- When `MailerAudienceSyncService.SyncAsync` runs (via the daily Hangfire job or the on-demand admin button) → one `MailerLiteAudienceSyncCompleted` audit entry with counts (no PII). Per-row ML mutations are not separately audited.

## Cross-Section Dependencies

- **Profiles**: reads `IUserEmailService.FindVerifiedEmailWithUserAsync`, `FindAnyUserIdByEmailAsync`, `DeleteEmailAsync`, `GetPrimaryEmailsByUserIdsAsync`; reads/writes `ICommunicationPreferenceService.GetAsync` / `UpdatePreferenceAsync` / `GetCountByCategoryAndStateAsync`.
- **Users**: writes via `IAccountProvisioningService.FindOrCreateUserByEmailAsync`; reads `IUserService.GetByIdAsync` (tombstone follow), `IUserService.GetCountByContactSourceAsync`.
- **Tickets**: `ITicketQueryService.GetUserIdsWithTicketsAsync` — audience-side ticket-holder enumeration for `TicketNoShiftsAudience`, `HasTicketAudience`, and `MarketingNoTicketAudience`. Scoped to the active vendor event by the cached decorator (see `TicketSyncState.VendorEventId`).
- **Shifts**: `IShiftView.GetUsersAsync` + `IUserService.GetAllUserInfosAsync` — cached per-user shift signups, used by `TicketNoShiftsAudience` and `HasShiftAudience` (encode Pending/Confirmed-on-active-event via `ShiftUserView.HasShift`).
- **Users**: `IUserService.GetAllUserInfosAsync` — audience-side enumeration of explicit Marketing opt-ins for `MarketingAudience` and `MarketingNoTicketAudience` (`UserInfo.MarketingOptedOut == false`).
- **AuditLog**: writes via `IAuditLogService.LogAsync` (job overload).

## Architecture

**Owning services:** `MailerImportService`, `MailerAudienceSyncService`, `MailerLiteClient`
**Owned tables:** _(none — MailerLite is read-write for groups owned by Humans; in-Humans writes route through other sections' services)_
**Status:** (A) Migrated — new section, born §15-compliant on 2026-05-12; outbound + audience framework added 2026-05-14.

- Services live in `Humans.Application.Services.Mailer/` and never import `Microsoft.EntityFrameworkCore`. `MailerLiteClient` lives in `Humans.Infrastructure.Services.Mailer/` (it owns the `HttpClient` + JSON parsing). `MailerAudienceSyncJob` lives in `Humans.Infrastructure.Jobs/`.
- **Decorator decision** — no caching decorator. Rationale: admin-only, sequential, runs by hand; one DB count per dashboard load is fine at 500 users.
- **Cross-section calls** — `IUserEmailService`, `IAccountProvisioningService`, `ICommunicationPreferenceService`, `IUserService`, `ITicketQueryService`, `IShiftView`, `IAuditLogService`.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/MailerArchitectureTests.cs` pins: namespace, no-EF on `MailerImportService` and `MailerAudienceSyncService`, allowed-write surface on `IMailerLiteService`, audience group-name prefix + uniqueness, and no cross-section repository injection in `MailerImportService`. `MailerLiteClientWriteGuardTests` pins the runtime "Humans - " prefix guard.
