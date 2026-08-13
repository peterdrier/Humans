<!-- freshness:triggers
  src/Sections/Humans.Campaigns/**
  src/Sections/Humans.Campaigns.Contracts/**
-->
<!-- freshness:flag-on-change
  Campaign lifecycle, code import/grant rules, wave email triggers, and unsubscribe handling — review when Campaign service/entities/controller change.
-->

# Campaigns — Section Invariants

Bulk code-distribution campaigns: codes imported or generated, assigned to humans, delivered via email waves.

## Concepts

- A **Campaign** is a bulk code distribution effort — discount codes are assigned to humans and delivered via email waves.
- A **Campaign Code** is an individual code belonging to a campaign. Codes are imported in bulk (CSV) or generated via the ticket vendor.
- A **Campaign Grant** records the assignment of a specific code to a specific human.
- A **Wave** is a batch email send targeting a group of humans (typically by team) who have been granted codes but not yet notified.

## Data Model

### Campaign

**Table:** `campaigns`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Title | string | Campaign display name |
| Description | string? | Optional description |
| EmailSubject | string | Subject line template (supports `{{Name}}`) |
| EmailBodyTemplate | string | Markdown body template (supports `{{Code}}` and `{{Name}}`) |
| ReplyToAddress | string? | Optional Reply-To header for campaign emails |
| Status | CampaignStatus | Draft / Active / Completed |
| CreatedAt | Instant | When created |
| CreatedByUserId | Guid | FK → User — **FK only**, no nav |

**Aggregate-local navs:** `Campaign.Codes`, `Campaign.Grants`.

### CampaignCode

One row per individual code belonging to a campaign. Codes are imported in bulk; each is assigned to at most one user via a CampaignGrant.

**Table:** `campaign_codes`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| CampaignId | Guid | FK → Campaign |
| Code | string | The code value (unique per campaign) |
| ImportOrder | int | Monotonic per-campaign sequence assigned at import time; wave allocation orders by this for stable batch order |
| ImportedAt | Instant | When imported |

**Aggregate-local navs:** `CampaignCode.Campaign`, `CampaignCode.Grant`.

### CampaignGrant

Records the assignment of a specific code to a specific user.

**Table:** `campaign_grants`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| CampaignId | Guid | FK → Campaign |
| CampaignCodeId | Guid | FK → CampaignCode (unique — one grant per code) |
| UserId | Guid | FK → User — **FK only**, no nav |
| AssignedAt | Instant | When assigned |
| LatestEmailStatus | EmailOutboxStatus? | Status of most recent delivery attempt |
| LatestEmailAt | Instant? | Timestamp of most recent delivery attempt |
| RedeemedAt | Instant? | When the granted code was redeemed in a ticket purchase; null if unused. Set by `TicketSyncService` via `MarkGrantsRedeemedAsync` |

**Indexes:** unique `(CampaignCodeId)` (one grant per code) and unique `(CampaignId, UserId)` (one grant per user per campaign).

**Aggregate-local navs:** `CampaignGrant.Campaign`, `CampaignGrant.Code`.
Cross-domain nav `CampaignGrant.OutboxMessages` (Email) has been removed — Email outbox rows reference the grant by bare FK only, resolved through the Email section's services. Campaigns code never traversed it; email delivery goes through `IEmailOutboxService`.

### CampaignStatus

| Value | Description |
|-------|-------------|
| Draft | Codes can be imported; sending not yet active |
| Active | Sending waves is enabled |
| Completed | Campaign closed |

Stored as string (`HasConversion<string>()`, max length 20).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| TicketAdmin, Admin | View campaign details, generate discount codes via the ticket vendor |
| Admin | Full campaign management: create, edit, activate, complete campaigns. Import codes. Manage grants. Send campaign email waves |

## Invariants

- Campaign status follows: Draft then Active then Completed. `ActivateAsync` requires Draft + at least one code; `CompleteAsync` requires Active; `SendWaveAsync` requires Active.
- Vendor-generated codes can only be created while the campaign is in Draft status (controller enforces). CSV code import has no service-side status guard — the Campaign Detail view exposes the import form in both Draft and Active.
- Each code is unique per campaign (DB-enforced via unique `(CampaignId, Code)` index) and can be assigned to at most one human (DB-enforced via unique `CampaignCodeId` on grants).
- Each human can hold at most one grant per campaign (DB-enforced via unique `(CampaignId, UserId)` on grants).
- Wave allocation pulls available codes ordered by `CampaignCode.ImportOrder` so batch order is stable and reproducible.
- Campaign emails are queued through the email outbox system. Each grant tracks the status and timestamp of the most recent delivery attempt; failed enqueues flip the single grant to `Failed` (the loop persists/enqueues one grant at a time so a mid-loop throw cannot orphan grants).
- `MessageCategory.CampaignCodes` is always-on (`IsAlwaysOn`) — there is no opt-out to gate on, confirmed intended (nobodies-collective/Humans#1032). `SendWaveAsync` / `PreviewWaveSendAsync` send/count every not-yet-granted team member; they do not call `ICommunicationPreferenceService`. Campaign-code mail carries no "Unsubscribe from these emails" footer link and no RFC 8058 `List-Unsubscribe` headers (`OutboxEmailService` skips unsubscribe stamping for always-on categories) — there is nothing for either to do.
- Campaign template substitution (`{{Code}}`, `{{Name}}`) HTML-encodes every value via `System.Net.WebUtility.HtmlEncode` before insertion into `Campaign.EmailBodyTemplate` / `EmailSubject` — recipient display names are user-controlled and would otherwise be an HTML-injection vector. Placeholder matching uses `StringComparison.Ordinal`. The full substitution vocabulary is `{{Code}}` and `{{Name}}`; new placeholders must be added at the renderer with the same encoding guard.

## Negative Access Rules

- TicketAdmin **cannot** create, edit, activate, or complete campaigns. They can only view details and generate codes.
- Regular humans and other roles have no access to campaign management.
- Humans see only their own grants on `/Profile/Me` (the "My Codes" card, sourced from `ICampaignService.GetActiveOrCompletedGrantsForUserAsync`); no human can see another human's grants outside the admin views.

## Triggers

- When a campaign wave is sent (`SendWaveAsync`), emails are queued to the outbox via `IEmailService.SendAsync(IEmailMessageFactory.CampaignCode(...))` for each eligible human, and a `CampaignReceived` in-app notification is dispatched (best-effort) to every recipient who actually received a grant.
- Legacy campaign-only unsubscribe tokens map to `MessageCategory.Marketing`, which is opt-outable; `ICommunicationPreferenceService.UpdatePreferenceAsync` flips that preference as normal. There is no live path to a `CampaignCodes` unsubscribe token — `OutboxEmailService` never generates one for an always-on category — and `UpdatePreferenceAsync`/`GuestController.CanUpdatePreference` would refuse the change regardless. (The legacy `User.UnsubscribedFromCampaigns` boolean still exists on the entity for GDPR export but is not read by any active gate.)
- When `TicketSyncService` detects a granted code redeemed in a ticket purchase, it calls `ICampaignService.MarkGrantsRedeemedAsync` to set `CampaignGrant.RedeemedAt`.
- When an enqueue throws during `SendWaveAsync` or `RetryAllFailedAsync`, the single offending grant is flipped to `Failed` so the next pass of `RetryAllFailedAsync` can pick it up.
- When an account merge accepts, `IUserMerge.ReassignAsync` (implemented by `CampaignService`) re-FKs `CampaignGrant.UserId` from source to target (collapsing duplicates where target already holds a grant for the same campaign). Called only by `IAccountMergeService.AcceptAsync` (Profiles section).

## Cross-Section Dependencies

- **Tickets:** `ITicketDiscountCodes` (`Humans.Tickets.Contracts`) — TicketAdmin can generate discount codes via the ticket vendor integration; Campaigns asks Tickets for codes through this leaf rather than reaching past it into the Base vendor port. Generation is invoked from the Campaign Detail page, not from the Tickets section.
- **Email:** `IEmailService.SendAsync` with `IEmailMessageFactory.CampaignCode` — composes and queues the campaign-code email through the outbox.
- **Profiles / Users:** `IUserEmailService.GetNotificationTargetEmailsAsync(IReadOnlyCollection<Guid>)` — resolves notification targets for grant emails; `IUserServiceRead.GetUserInfoAsync` / `GetUserInfosAsync` — recipient `DisplayName` for the email payload and code-tracking display; `IUnsubscribeService` (in `Humans.Application.Services.Users`) processes the public `/Unsubscribe/{token}` endpoint, validating legacy campaign-only tokens (mapped to `MessageCategory.Marketing`) before delegating opt-out to `ICommunicationPreferenceService.UpdatePreferenceAsync` — `CampaignService` itself does not call `ICommunicationPreferenceService`.
- **Notifications:** `INotificationService.SendAsync` — `CampaignReceived` in-app notifications for wave recipients.
- **Teams:** `ITeamService.GetActiveTeamOptionsAsync` (Send Wave team picker) and `ITeamService.GetTeamMembersAsync` (team-scoped wave targeting).
- **Profiles:** Called by `IAccountMergeService` (Profiles section) — `IUserMerge.ReassignAsync` (implemented by `CampaignService`) re-FKs `CampaignGrant` from source to target during account merge fold.

## Architecture

**Owning services:** `CampaignService`
**Owned tables:** `campaigns`, `campaign_codes`, `campaign_grants`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#546, 2026-04-22); own project since G5 (nobodies-collective/Humans#866). Everything but `Section` is `internal` (HUM0034); the cross-section surface is the leaf project `Humans.Campaigns.Contracts` — `ICampaignService`, `ICampaignServiceRead` and the code-tracking DTOs `TicketQueryService` reads.

- `CampaignService` lives in `Humans.Campaigns.Services` and depends only on Application-layer abstractions.
- `ICampaignRepository` (interface `src/Sections/Humans.Campaigns/Data/ICampaignRepository.cs`, impl `src/Sections/Humans.Campaigns/Data/CampaignRepository.cs`) is the only file that touches this section's tables via `DbContext`.
- **Decorator decision — no caching decorator.** Admin-only, low write/read volume.
- **Cross-section reads** route through `ITeamService.GetActiveTeamOptionsAsync` / `GetTeamMembersAsync` and `IUserEmailService.GetNotificationTargetEmailsAsync`, `IUserServiceRead.GetUserInfoAsync` / `GetUserInfosAsync` for display data. Outbound email queueing goes through `IEmailService.SendAsync` with `IEmailMessageFactory.CampaignCode` (the outbox service owns the email_outbox_messages table and applies opt-out/unsubscribe policy itself — `CampaignService` does not call `ICommunicationPreferenceService`).
- **Cross-domain navs removed:** `Campaign.CreatedByUserId` and `CampaignGrant.UserId` are bare Guid columns — no `CreatedByUser` / `User` nav property, and since G5 no DB-level FK constraint either: the section assembly cannot name `User`, so `CampaignConfiguration` / `CampaignGrantConfiguration` declare no relationship at all. All callers — including `TicketQueryService.GetCodeTrackingDataAsync` via `ICampaignService.GetCodeTrackingAsync` — resolve display names through `IUserService`. `CampaignGrant.OutboxMessages` (Email) is also gone — Email outbox rows reference the grant by bare FK only.
- **Architecture test** — `tests/Humans.Campaigns.Tests/Architecture/CampaignsArchitectureTests.cs`, alongside the cross-cutting analyzer coverage (`HUM0009`, `HUM0034`; `HUM0024` and `HUM0021` were retired in nobodies-collective/Humans#1278).

### Touch-and-clean guidance

- Do not add new cross-domain navs to `Campaign`, `CampaignCode`, or `CampaignGrant`. When adding fields, keep them scalar or aggregate-local only.
- New cross-section reads must go through the owning service interface; never `_dbContext`.
