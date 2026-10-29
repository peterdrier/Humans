<!-- freshness:triggers
  src/Sections/Humans.Campaigns/**
  src/Sections/Humans.Email/Services/OutboxEmailService.cs
  src/Sections/Humans.Tickets/Services/TicketSyncService.cs
  src/Sections/Humans.Email/Services/UnsubscribeService.cs
  src/Sections/Humans.Email/Controllers/GuestAccountController.cs
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
- A **Wave** is a batch send to the members of one chosen team who hold no grant from this campaign yet: the wave grants each the next free code, queues the email, and sends the in-app notice.

## Data Model

### Campaign

**Table:** `campaigns`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| Title | string | Campaign display name |
| Description | string? | Optional description |
| EmailSubject | string | Subject line template (supports `{{Code}}` and `{{Name}}`) |
| EmailBodyTemplate | string | Markdown body template (supports `{{Code}}` and `{{Name}}`) |
| ReplyToAddress | string? | Optional Reply-To header for campaign emails |
| Status | CampaignStatus | Draft / Active / Completed |
| CreatedAt | Instant | When created |
| CreatedByUserId | Guid | Bare cross-section id (User) — no FK constraint, no nav |

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
| UserId | Guid | Bare cross-section id (User) — no FK constraint, no nav |
| AssignedAt | Instant | When assigned |
| LatestEmailStatus | EmailOutboxStatus? | Retained, unwritten since nobodies-collective/Humans#1243 dropped delivery tracking; holds old grants' last status, still handed over by the GDPR export. Kept until a drop is approved |
| LatestEmailAt | Instant? | Retained, unwritten and unread since nobodies-collective/Humans#1243. Kept until a drop is approved |
| RedeemedAt | Instant? | When the granted code was redeemed in a ticket purchase; null if unused. Set by `TicketSyncService` via `MarkGrantsRedeemedAsync` |

**Indexes:** unique `(CampaignCodeId)` (one grant per code) and unique `(CampaignId, UserId)` (one grant per user per campaign).

**Aggregate-local navs:** `CampaignGrant.Campaign`, `CampaignGrant.Code`. No cross-domain navs. A grant does not track its email's delivery: that lives in Email's outbox, which no longer records the grant either.

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
| TicketsAdmin, Admin | View campaign details, generate discount codes via the ticket vendor |
| Admin | Full campaign management: create, edit, activate, complete campaigns. Import codes. Manage grants. Send campaign email waves |

## Invariants

- Create/edit validate trimmed title (200), description (2000), email subject (1000) and Reply-To (320) lengths before persistence. Nonblank Reply-To must be an email address; blank stays optional. Invalid forms redisplay field errors and preserve existing campaign data.
- Placeholder expansion can make a valid subject template exceed the email queue's 1000-character limit. All campaign sends retain the complete rendered subject as an HTML-encoded leading body paragraph in that case, while shortening the subject header to 1000 Unicode characters including an ellipsis. Shorter subjects and their bodies remain unchanged; codes are preserved even when the authored body omits `{{Code}}`.

- Index, edit, detail and Send Wave GETs pass request cancellation to their read-only service/repository calls, so abandoned pages stop loading data. Mutating requests retain their existing cancellation boundaries.

- Campaign status follows: Draft then Active then Completed. `ActivateAsync` requires Draft + at least one code; `CompleteAsync` requires Active; `SendWaveAsync` requires Active. Wrong-state attempts (double-clicks, stale forms, the Send Wave GET on a non-Active campaign) return error results the controller surfaces as an error toast + redirect, never a 500.
- Vendor-generated codes can only be created while the campaign is in Draft status (service enforces). CSV code import has no service-side status guard — the Campaign Detail view exposes the import form in both Draft and Active. Code uploads are capped at 1 MB and their reads observe request cancellation. CSV files use the shared CSV reader, with one column and no header; quoted commas, quotes and newlines remain part of one code. Malformed or multi-column CSV is refused before importing any row. TXT retains one literal code per line, including commas and quotes. Trimmed codes are limited to 200 Unicode characters, matching the existing database column; an oversized code refuses the entire import before persistence or success audit. Both upload and generation actions surface import refusals as errors.
- Code-generation POSTs require an explicitly submitted discount value (including zero), resolve the actor, then reject binding errors with HTTP 400 and a Warning before vendor generation, local import or audit. Explicitly bound zero values keep the existing service behavior.
- Undefined discount kinds, including numeric enum values, return InvalidDiscountType before vendor generation or code import.
- Each code is unique per campaign (DB-enforced via unique `(CampaignId, Code)` index) and can be assigned to at most one human (DB-enforced via unique `CampaignCodeId` on grants).
- Each human can hold at most one grant per campaign (DB-enforced via unique `(CampaignId, UserId)` on grants).
- Wave allocation pulls available codes ordered by `CampaignCode.ImportOrder` so batch order is stable and reproducible.
- Campaign emails are queued through the email outbox system. A grant records no delivery status (nobodies-collective/Humans#1243): an admin who needs to know whether a code reached someone reads the outbox, and Resend re-queues it. The loop persists/enqueues one grant at a time, so a mid-loop throw cannot orphan grants; request cancellation propagates.
- `MessageCategory.CampaignCodes` is always-on (`IsAlwaysOn`) — there is no opt-out to gate on, confirmed intended (nobodies-collective/Humans#1032). `SendWaveAsync` / `PreviewWaveSendAsync` send/count every not-yet-granted team member; they do not call `ICommunicationPreferenceService`. Campaign-code mail carries no "Unsubscribe from these emails" footer link and no RFC 8058 `List-Unsubscribe` headers (`OutboxEmailService` skips unsubscribe stamping for always-on categories) — there is nothing for either to do.
- Campaign template substitution (`{{Code}}`, `{{Name}}`) keeps inserted code/name values literal: opaque markers pass through Markdown parsing, then HTML-encoded values replace them before the shared sanitizer checks the complete HTML and URLs. User-controlled names and imported codes cannot add Markdown links/images or change code-span contents. Authored Markdown formatting, links and HTTPS images remain supported. `EmailSubject` substitutes unencoded plain text. Placeholder matching is exact-case and single-pass; inserted values containing placeholder text are not expanded again. The full substitution vocabulary is `{{Code}}` and `{{Name}}`; the body uses the shared `_EmailComposer` and `SanitizedMarkdownRenderer`.

## Negative Access Rules

- TicketsAdmin **cannot** create, edit, activate, or complete campaigns. They can only view details and generate codes.
- Regular humans and other roles have no access to campaign management.
- Humans see only their own grants on `/Profile/Me` (the "My Codes" card, `MyCodesViewComponent`, sourced from `ICampaignService.GetActiveOrCompletedGrantsForUserAsync`). The card renders only for the `Self` view mode and only when the signed-in user is the profile's owner, so it is absent on another member's profile for every role. No human can see another human's grants outside the admin card.
- The "Campaign Codes" card (`CampaignCodesViewComponent`, every grant whatever the campaign's status) renders only in the `Admin` view mode for a viewer passing `HumanAdminBoardOrAdmin`, the same policy as the host page; any other viewer or audience gets nothing.

## Triggers

- Campaign notice titles fit the 200-Unicode-character notification limit; an oversized title is shortened with an ellipsis and retained in full before the body text.
- When a campaign wave is sent (`SendWaveAsync`), emails are queued to the outbox via `IEmailService.SendAsync(CampaignsEmails.CampaignCode(...))` for each eligible human, and a `CampaignReceived` in-app notification is dispatched (best-effort) to every recipient who actually received a grant. Copy is rendered from Campaigns resources in each recipient’s supported preferred language (English fallback), grouped by language with isolated dispatch failures. The notice links to My Codes in `/Profile/Me`, where the persisted grant is available even if email enqueue failed; it never promises email delivery. Campaigns sends full copy to Notifications, which bounds stored preview titles to 200 Unicode characters and preserves oversized titles in the body.
- Legacy campaign-only unsubscribe tokens map to `MessageCategory.Marketing`, which is opt-outable; `ICommunicationPreferenceService.UpdatePreferenceAsync` flips that preference as normal. There is no live path to a `CampaignCodes` unsubscribe token — `OutboxEmailService` never generates one for an always-on category — and `UpdatePreferenceAsync`/`GuestAccountController.CanUpdatePreference` would refuse the change regardless. (The legacy `User.UnsubscribedFromCampaigns` boolean still exists on the entity for GDPR export but is not read by any active gate.)
- When `TicketSyncService` detects a granted code redeemed in a ticket purchase, it calls `ICampaignWrite.MarkGrantsRedeemedAsync` to set `CampaignGrant.RedeemedAt`.
- When an enqueue throws during `SendWaveAsync`, the error is logged for that grant and the wave goes on; an admin can Resend that grant. A single-grant resend propagates recipient lookup, message preparation and enqueue failures to the admin, so a resend that queued nothing never reports success.
- When an account merge accepts, `IUserMerge.ReassignAsync` (implemented by `CampaignService`) re-FKs `CampaignGrant.UserId` from source to target (collapsing duplicates where target already holds a grant for the same campaign). Called only by `IAccountMergeService.AcceptAsync` (Users section).

Admin mutations write audit entries after the business save, attributed to the
acting admin: creation, configuration edits, manual/generated code imports,
activation, completion, wave assignments and resend queueing. Each saved
wave grant is audited individually, so an interrupted batch retains
its completed actions. Grant entries link the recipient user; no code value or
email template is copied into audit. Refused mutations emit no success entry.

## Cross-Section Dependencies

- **Tickets:** `ITicketDiscountCodes` (`Humans.Tickets.Contracts`) — TicketsAdmin can generate discount codes via the ticket vendor integration; Campaigns asks Tickets for codes through this leaf rather than reaching past it into the Base vendor port. Generation is invoked from the Campaign Detail page, not from the Tickets section.
- **Email:** transport only. Campaigns owns its one template — `CampaignsEmails` (internal) builds the `EmailMessage` from the campaign's own subject and markdown body (the builder reads no resx), and `CampaignsEmailPreviews` (`IEmailPreviewContributor`, registered in `Section.Register`) lists it at `/Email/EmailPreview` (`memory/architecture/email-templates-live-in-sender.md`). `IEmailService.SendAsync` queues the message through the outbox.
- **Users:** `IUserEmailService.GetNotificationTargetEmailsAsync(IReadOnlyCollection<Guid>)` — resolves notification targets for grant emails; `IUserService.GetUserInfoAsync` / `GetUserInfosAsync` — recipient `BurnerName` for the email payload and code-tracking display; `IUnsubscribeService` (Email section, `Humans.Email.Services.UnsubscribeService`) processes the public `/Email/Unsubscribe/{token}` endpoint, validating legacy campaign-only tokens (mapped to `MessageCategory.Marketing`) before delegating opt-out to `ICommunicationPreferenceService.UpdatePreferenceAsync` — `CampaignService` itself does not call `ICommunicationPreferenceService`. Called by `IAccountMergeService` (Users section) — `IUserMerge.ReassignAsync` (implemented by `CampaignService`) re-FKs `CampaignGrant` from source to target during account merge fold.
- **Gdpr:** `IUserDataContributor` (grant export resolves archived account IDs to the survivor; erasure keeps its per-ID scope) and `IUserMerge` (`Humans.Gdpr.Contracts`), both implemented by `CampaignService`.
- **AuditLog:** `IAuditLogService.LogAsync` records persisted admin mutations.
- **Notifications:** `INotificationEmitter.SendAsync` — `CampaignReceived` in-app notifications for wave recipients. Its sources are declared in `CampaignsNotificationSources` (`src/Sections/Humans.Campaigns/CampaignsNotificationSources.cs`), one `NotificationSourceDefinition` each: `CampaignReceived` (informational, to campaign recipients: campaign code granted).
- **Teams:** `ITeamService.GetTeamsAsync` (Send Wave team picker) and `ITeamService.GetTeamAsync` (team-scoped wave targeting).

## Architecture

**Owning services:** `CampaignService`
**Owned tables:** `campaigns`, `campaign_codes`, `campaign_grants`
**Status:** (A) Own project. Everything but `Section` is `internal` (HUM0034); the cross-section surface is the `Contracts/` folder (namespace `Humans.Campaigns.Contracts`).

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `ICampaignService` | 3 | Read contract: `GetCodeTrackingAsync` (Tickets); `GetActiveOrCompletedGrantsForUserAsync` and `GetAllGrantsForUserAsync` are read by Campaigns' own member and admin cards (below). DTOs only. |
| `ICampaignWrite` | 1 | `MarkGrantsRedeemedAsync` (Tickets sync marks redeemed codes). Unavoidable: it is driven by another section's job. |

`CampaignService` implements the internal `ICampaignManagement` (`ICampaignService` + `ICampaignWrite`); DI registers each face to the same instance. Campaign/code CRUD, activation, wave sending and resend are not public: the section's controller injects the concrete service.

- `CampaignService` lives in `Humans.Campaigns.Services` and depends only on Application-layer abstractions.
- `ICampaignRepository` (interface `src/Sections/Humans.Campaigns/Data/ICampaignRepository.cs`, impl `src/Sections/Humans.Campaigns/Data/CampaignRepository.cs`) is the only file that touches this section's tables via `DbContext`.
- **Decorator decision — no caching decorator.** Admin-only, low write/read volume.
- **Cross-section reads** route through `ITeamService.GetTeamsAsync` / `GetTeamAsync` and `IUserEmailService.GetNotificationTargetEmailsAsync`, `IUserService.GetUserInfoAsync` / `GetUserInfosAsync` for display data. Outbound email queueing goes through `IEmailService.SendAsync` with `CampaignsEmails.CampaignCode` (the outbox service owns the email_outbox_messages table and applies opt-out/unsubscribe policy itself — `CampaignService` does not call `ICommunicationPreferenceService`).
- **No cross-domain navs:** `Campaign.CreatedByUserId` and `CampaignGrant.UserId` are bare Guid columns — no nav property, no DB-level FK constraint: the section assembly cannot name `User`, so `CampaignConfiguration` / `CampaignGrantConfiguration` declare no relationship at all. All callers — including `TicketQueryService.GetCodeTrackingDataAsync` via `ICampaignService.GetCodeTrackingAsync` — resolve display names through `IUserService`.
- **Architecture test** — `tests/Humans.Campaigns.Tests/Architecture/CampaignsArchitectureTests.cs`, alongside the cross-cutting analyzer coverage (`HUM0009`, `HUM0034`).

### User parts

`SectionUserParts` (`IUserPart`) contributes two cards to Users' pages; Users names neither:

| Slot | Component | Audience |
|---|---|---|
| `UserPartSlots.Profile` | `MyCodesViewComponent` ("My Codes", member-facing, `Campaigns_MyCodes_*` keys in all six cultures) | Own profile only |
| `UserPartSlots.AdminDetail` | `CampaignCodesViewComponent` ("Campaign Codes", English, admin-only) | `/Users/Admin/{id}`, `HumanAdminBoardOrAdmin` |

Both render nothing when the member has no grants. The cards add no routes, nav entries or data; they are reached from the profile and admin-detail pages.

### Touch-and-clean guidance

- Do not add new cross-domain navs to `Campaign`, `CampaignCode`, or `CampaignGrant`. When adding fields, keep them scalar or aggregate-local only.
- New cross-section reads must go through the owning service interface; never `_dbContext`.
