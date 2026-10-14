# Campaigns — Target Shape

Written fresh each doctor run (section-doctor, Phase 3c) before any scan runs; diffed
against the previous run's copy.

## What the section does

Admins hand out one-per-person discount codes in bulk. An admin creates a campaign with an
email template, loads codes into it (CSV upload, or asks the ticket vendor to mint some while
it is still a draft), switches it on, and then — team by team — fires "waves": every member of
the chosen team who doesn't already hold a code from this campaign gets the next free code, an
email, and an in-app notice in their own language. The admin watches delivery (sent/failed,
with per-grant resend and a retry-all), and the ticket-sync job later marks which codes were
actually used in a purchase. Every admin action lands in the audit log under the admin who
took it. Members see their own codes on their profile; campaign mail is always-on (no
unsubscribe). When an account merge folds two people together, the survivor keeps one grant
per campaign. Grants are personal data: exported for GDPR, hard-deleted on erasure.

## The shapes

| Shape | Members |
|---|---|
| Campaign CRUD + lifecycle (admin pages) | Create, Edit, Index, Detail, Activate, Complete |
| Getting codes in | ImportCodes (CSV), GenerateCodes (vendor, Draft-only) |
| Sending | SendWave (preview + confirm), Resend (one grant), RetryAllFailed |
| Copy it owns | the campaign-code email (`CampaignsEmails`, gallery sample via `CampaignsEmailPreviews`), the localized `CampaignReceived` notice (`CampaignsResource`) |
| Reads other assemblies render | `GetActiveOrCompletedGrantsForUserAsync` (profile), `GetAllGrantsForUserAsync` (admin user detail), `GetCodeTrackingAsync` (Tickets dashboard) |
| Writes other assemblies drive | `MarkGrantsRedeemedAsync` (ticket sync), `UpdateGrantEmailStatusAsync` (email outbox) |
| Platform obligations | GDPR export/erasure (`IUserDataContributor`), account-merge fold (`IUserMerge`) |

The CRUD/lifecycle, code-loading and sending shapes are the internal admin app; the
cross-assembly reads, writes and platform obligations are the section's service to the rest
of the platform. The `Contracts/` folder carries exactly that outward surface (plus the DTOs it
returns) and nothing of the admin app — that split is correct and worth defending.

## Structure

The shapes imply what is already there, and nothing more:

- A controller (admin routes only), a service, a repository, and a DbContext over
  `campaigns`, `campaign_codes`, `campaign_grants`.
- A `Contracts/` folder with `ICampaignService : ICampaignServiceRead` and the cross-assembly
  DTOs; everything else `internal`.
- One code-loading path: CSV import and vendor generation both end in the same
  "append codes after the highest `ImportOrder`, skip ones already present, audit" step.
- A pure email builder plus its gallery contributor; a resx set that carries only the
  member-facing notice copy.
- Admin pages plus a shared Create/Edit form-fields partial; view-local sorting/formatting;
  badge colours from the shared `EnumBadgeMap` registry.
- No caching decorator (admin-only, cold), no background jobs of its own (ticket sync and
  outbox live elsewhere and call in through the contract).

The one structural delta from that shape is the duplicated code-loading step (vendor
generation re-implements the CSV import body). The rest of the residual weight is prose:
doc and comment claims that drifted when the contracts leaf folded in and the notice copy
gained a resx set.

## Invariants

- Lifecycle is one-way: Draft → Active → Completed. Activate requires Draft and a code
  (`CampaignService.cs:368`, `:371`); Complete requires Active (`:395`); SendWave requires
  Active (`:438`); vendor generation requires Draft (`:343`).
- A code is unique within its campaign (`CampaignCodeConfiguration.cs:16`) and granted at most
  once (`CampaignGrantConfiguration.cs:25`).
- A person holds at most one grant per campaign (`CampaignGrantConfiguration.cs:24`); the merge
  fold keeps the target's grant on collision (`CampaignRepository.cs:353`).
- Wave allocation takes free codes in `ImportOrder` (`CampaignRepository.cs:119`).
- SendWave aborts up front when eligible members outnumber free codes (`CampaignService.cs:457`).
- A wave persists and enqueues one grant at a time; an enqueue failure flips only that grant
  to Failed (`CampaignService.cs:520`), which RetryAllFailed picks up
  (`CampaignRepository.cs:183`).
- Template substitution HTML-encodes `{{Code}}` and `{{Name}}` in the body
  (`CampaignsEmails.cs:24`); the subject substitutes both unencoded (`CampaignsEmails.cs:32`).
- Campaign-code mail is always-on: `MessageCategory.CampaignCodes` is in the always-on set
  (`MessageCategory.cs:72`), so the outbox stamps no unsubscribe (`OutboxEmailService.cs:51`).
- Every admin mutation writes an audit entry attributed to the acting admin after its save
  (`CampaignService.cs:67`, `:169`, `:289`, `:382`, `:401`, `:500`, `:582`, `:691`).
- Admin holds every page and mutation except Detail and vendor generation, which are
  `TicketAdminOrAdmin` (`CampaignController.cs:126`, `:179`).
- Grants are exported per user and hard-deleted on erasure (`CampaignService.cs:773`, `:798`).

## Seams

none

## Deliberately not done

- No localization of the admin pages — admin-exempt (`memory/code/localization-admin-exempt.md`).
  The resx set is for the member-facing notice only; don't add page keys.
- No caching decorator — admin pages, not hot.
- No `UpdatedAt` on `CampaignGrant` — `ReassignGrantsToUserAsync` deliberately discards the
  merge fold's timestamp argument (signature parity across `IUserMerge` implementers).
- No status guard on CSV import — the Detail page offers it in Draft and Active, on purpose;
  the service accepts any status.
- No actor on grant or outbox rows — the audit log carries the actor.

## Load-bearing weirdness

- `ICampaignRepository` is Singleton over `IDbContextFactory` — the factory injection is the
  point; an architecture test pins it.
- The section's own controller injects the concrete `CampaignService`, not the interface: the
  contract carries only the members other assemblies call; the rest stay internal.
- `EmailOutboxStatus` on the contract DTOs is Base vocabulary re-exported, not owned.
- `CampaignStatus` is stored as a string — renaming a member needs a data migration; a local
  enum-stability test pins the names (the central one can't see an internal enum).
- Tickets is reached through `ITicketDiscountCodes` (Tickets' contracts leaf), never the Base
  vendor port directly; the leaf edge keeps the Tickets↔Campaigns pair acyclic.
- The wave-send preview is a GET with `teamId` (no state), so the POST re-derives eligibility
  rather than trusting the preview.
- A single-grant resend that cannot resolve its recipient marks the grant Failed and rethrows to
  the admin — it never leaves a Queued grant with no outbox row.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-08-29 | First doctoring: false doc/comment claims fixed, badge + form dedup, cross-assembly test coverage added | peterdrier/Humans#1573 |
