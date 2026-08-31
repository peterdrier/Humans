# Campaigns — Target Shape

Written fresh each doctor run (section-doctor, Phase 3c) before any scan runs; diffed
against the previous run's copy.

## What the section does

Admins hand out one-per-person discount codes in bulk. An admin creates a campaign with an
email template, loads codes into it (CSV paste-upload, or asks the ticket vendor to mint
some), switches it on, and then — team by team — fires "waves": every member of the chosen
team who doesn't already hold a code from this campaign gets the next free code, an email,
and an in-app notification. The admin watches delivery (sent/failed, with per-grant resend
and a retry-all), and the ticket-sync job later marks which codes were actually used in a
purchase. Members see their own codes on their profile; campaign mail is always-on (no
unsubscribe). When an account merge folds two people together, the survivor keeps one grant
per campaign. Grants are personal data: exported for GDPR, hard-deleted on erasure.

## The shapes

| Shape | Members |
|---|---|
| Campaign CRUD + lifecycle (admin pages) | Create, Edit, Index, Detail, Activate, Complete |
| Getting codes in | ImportCodes (CSV), GenerateCodes (vendor, Draft-only) |
| Sending | SendWave (preview + confirm), Resend (one grant), RetryAllFailed |
| Reads other assemblies render | GetActiveOrCompletedGrantsForUserAsync (profile), GetAllGrantsForUserAsync (admin user detail), GetCodeTrackingAsync (Tickets dashboard) |
| Writes other assemblies drive | MarkGrantsRedeemedAsync (ticket sync), UpdateGrantEmailStatusAsync (email outbox) |
| Platform obligations | GDPR export/erasure (IUserDataContributor), account-merge fold (IUserMerge) |

Six shapes; the first three are the internal admin app, the last three are the section's
service to the rest of the platform. The contracts leaf carries exactly the last-three
surface (plus the DTOs they return) and nothing of the first three — that split is correct
and worth defending.

## Structure

The shapes imply what is already there, and nothing more:

- One controller (admin routes only), one service, one repository, one DbContext over three
  tables (`campaigns`, `campaign_codes`, `campaign_grants`).
- A contracts leaf with the two interfaces (`ICampaignService : ICampaignServiceRead`) and
  the cross-assembly DTOs; everything else `internal`.
- Five pages plus a shared Create/Edit form-fields partial; view-local sorting/formatting;
  badge colours from the shared `EnumBadgeMap` registry, not view-local switch tables.
- No caching decorator (admin-only, cold), no background jobs of its own (ticket sync and
  outbox live elsewhere and call in through the contract).

There is no structural refactor this section needs. Its residual weight is prose: doc
claims that drifted from the code, comments narrating history, and a handful of dead
record fields / project references.

## Invariants

- Lifecycle is one-way: Draft → Active → Completed. Activate requires Draft + ≥1 code;
  Complete requires Active; SendWave requires Active; vendor generation requires Draft
  (service-enforced). CSV import is deliberately allowed in Draft and Active.
- A code belongs to one campaign, is unique within it (DB: unique `(CampaignId, Code)`),
  and is granted at most once (DB: unique `CampaignCodeId` on grants).
- A person holds at most one grant per campaign (DB: unique `(CampaignId, UserId)`); the
  merge fold preserves this (target wins on collision).
- Wave allocation is deterministic: free codes ordered by `ImportOrder`.
- A wave never partially orphans: per-grant persist + enqueue; an enqueue failure flips only
  that grant to Failed, and RetryAllFailed can always pick it up.
- SendWave aborts up front when eligible members outnumber free codes.
- Template substitution (`{{Code}}`, `{{Name}}`) HTML-encodes both values in the body;
  the subject substitutes unencoded (plain text, never rendered as HTML). Names are
  user-controlled input.
- Campaign-code mail is always-on (`MessageCategory.CampaignCodes`): no preference gate, no
  unsubscribe link/header — confirmed intended (nobodies-collective/Humans#1032).
- Only Admin mutates; TicketAdmin may view Detail and generate vendor codes; members see
  only their own grants.
- Grants are exported per-user (GDPR) and hard-deleted on erasure.

## Seams

none

## Deliberately not done

- No localization: the section is admin-only and ships no resource set at all (§15.3b, with
  Finance and Gate). Don't add keys.
- No caching decorator — admin pages, not hot.
- No audit-log entries for campaign mutations; the grant rows themselves (status, timestamps,
  actor-free) plus email outbox rows are the record so far. Adding audit would be new
  surface, not cleanup.
- No `UpdatedAt` on `CampaignGrant` — `ReassignGrantsToUserAsync` deliberately discards the
  merge fold's timestamp argument (signature parity across `IUserMerge` implementers).
- No status guard on CSV import — Draft and Active both accept imports, on purpose.

## Load-bearing weirdness

- `ICampaignRepository` is Singleton over `IDbContextFactory` (§15b) — the factory injection
  is the point; an architecture test pins it.
- The section's own controller injects the concrete `CampaignService`, not the interface:
  the contract leaf carries only the members other assemblies call; the rest stay
  internal (design §15 step 5).
- `EmailOutboxStatus` on the contract DTOs is Base vocabulary re-exported, not owned; the
  leaf references Base only.
- `CampaignStatus` is stored as a string — renaming a member needs a data migration; a
  local enum-stability test pins the names (the central one can't see an internal enum).
- Tickets is reached through `ITicketDiscountCodes` (Tickets' contracts leaf), never the
  Base vendor port directly; the leaf edge keeps the Tickets↔Campaigns pair acyclic.
- The one wave-send preview is a GET with `teamId` (no state), so the POST re-derives
  eligibility rather than trusting the preview.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-08-29 | First doctoring: false doc/comment claims fixed, badge + form dedup, cross-assembly test coverage added | peterdrier/Humans#pending |
