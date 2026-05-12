# Mailer — Section Invariants

Orchestrates Humans ↔ MailerLite synchronisation. Currently inbound-only; outbound is the immediate next slice (see Invariants below).

## Concepts

- **MailerLite subscriber** — a row in ML's `subscribers` collection. Has `status ∈ {active, unsubscribed, unconfirmed, bounced, junk}` and `subscribed_at` / `unsubscribed_at` / `opted_in_at` timestamps.
- **Import plan** — the classified result of pulling every ML subscriber and matching against Humans's user/email/preference state. Built fresh on every preview/commit; never persisted between runs.
- **Apply** — executes an import plan: creates contacts, attaches verified users, deletes unverified UserEmail rows that block contact creation, updates Marketing preferences per the conflict rule, writes one summary audit.

## Data Model

Mailer owns no tables. MailerLite is the system of record for subscriber state; Humans reads via the API. Classifier writes route through other sections' services (`UserEmailService`, `AccountProvisioningService`, `CommunicationPreferenceService`, `UserService`).

## Routing

- `/Mailer/Admin` — dashboard
- `/Mailer/Admin/Import` — preview (GET)
- `/Mailer/Admin/Import/Commit` — apply (POST)

All routes are `AdminOnly`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | none — section is admin-only |
| Admin | view dashboard, run import preview, commit import |

## Invariants

- `IMailerLiteService` exposes only GET-shaped methods. No `Create`/`Update`/`Delete`/`Upsert`/`Add`/`Remove`/`Set`/`Post`/`Put`/`Patch` prefixes. Pinned by `MailerArchitectureTests.IMailerLiteService_HasNoWriteMethods`.
- `MailerLiteClient` runtime-rejects any non-GET HTTP method with `InvalidOperationException`.
- `MailerImportService` lives in `Humans.Application` and never references `Microsoft.EntityFrameworkCore`. Pinned by architecture test.
- Every write to `CommunicationPreference[Marketing]` goes through `CommunicationPreferenceService.UpdatePreferenceAsync` and produces a `CommunicationPreferenceChanged` audit entry on real state changes (not idempotent confirms).
- `ApplyAsync` is idempotent: a second run against unchanged ML+Humans state writes zero per-row entries and exactly one `MailerLiteReconciliationCompleted` summary entry.
- Bounced/junk subscribers always set `OptedOut = true` regardless of any Humans-side timestamp. Delivery facts override preferences.
- For non-bounce subscribers, Humans state wins only when the prior write's `UpdateSource ∈ {Profile, Guest, MagicLink, OneClick}` AND `UpdatedAt > mlActionAt`.
- `CommunicationPreference.SubscribedAt` is stamped on first known opt-in and never overwritten while non-null.
- Inbound-only is a known compliance gap. **Outbound is the next slice and must ship before any other Mailer feature.** Drift in the Humans-newer-than-ML direction is mitigated only by the dashboard drift report (§6.1 of the spec) and manual admin remediation in the ML UI until outbound lands.

## Negative Access Rules

- Non-admins **cannot** access any `/Mailer/Admin/*` route.
- `IMailerLiteService` **cannot** be extended with write methods without removing the architecture-test pin in the same PR.
- `MailerImportService` **cannot** inject `HumansDbContext` or any non-Mailer repository (it goes through service interfaces).
- Code outside `CommunicationPreferenceService` **cannot** write to `communication_preferences` directly.

## Triggers

- When admin commits an import → one `MailerLiteReconciliationCompleted` audit entry with counts (no PII).
- When `ApplyAsync` flips `Marketing.OptedOut` → existing `CommunicationPreferenceChanged` audit fires through `CommunicationPreferenceService`.
- When `ApplyAsync` creates a contact → existing `ContactCreated` audit through `AccountProvisioningService`.

## Cross-Section Dependencies

- **Profiles**: reads `IUserEmailService.FindVerifiedEmailWithUserAsync`, `FindAnyUserIdByEmailAsync`, `DeleteEmailAsync`; reads/writes `ICommunicationPreferenceService.GetAsync` / `UpdatePreferenceAsync` / `GetCountByCategoryAndStateAsync`.
- **Users**: writes via `IAccountProvisioningService.FindOrCreateUserByEmailAsync`; reads `IUserService.GetByIdAsync` (tombstone follow), `IUserService.GetCountByContactSourceAsync`.
- **AuditLog**: writes via `IAuditLogService.LogAsync` (job overload).

## Architecture

**Owning services:** `MailerImportService`, `MailerLiteClient`
**Owned tables:** _(none — MailerLite is read-only; classifier writes through other sections' services)_
**Status:** (A) Migrated — new section, born §15-compliant on 2026-05-12.

- Services live in `Humans.Application.Services.Mailer/` and never import `Microsoft.EntityFrameworkCore`. `MailerLiteClient` lives in `Humans.Infrastructure.Services.Mailer/` (it owns the `HttpClient` + JSON parsing).
- **Decorator decision** — no caching decorator. Rationale: admin-only, sequential, runs by hand; one DB count per dashboard load is fine at 500 users.
- **Cross-section calls** — `IUserEmailService`, `IAccountProvisioningService`, `ICommunicationPreferenceService`, `IUserService`, `IAuditLogService`.
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/MailerArchitectureTests.cs` pins: namespace, no-EF, GET-only client interface, no cross-section repository injection in `MailerImportService`.
