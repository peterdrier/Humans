# Campaigns — Target Shape

Regenerated every section-doctor run (Phase 3c), before any scan. History table at the bottom.

## 1. What the section does

An admin creates a bulk code-distribution effort, loads unique one-per-person codes into it
(CSV upload, or generated through the ticket vendor while still in draft), turns it on, and
sends codes out team by team: each send assigns a free code to every team member who doesn't
already hold one and emails it to them. Failed deliveries can be retried one at a time or all
at once. Recipients see their codes on their own profile; admins see per-campaign delivery and
redemption counts, updated when the ticket system spots a code being used. When the effort is
over the admin closes it. Grants follow the person through account merges, appear in their
GDPR export, and are hard-deleted on erasure.

## 2. The shapes

| Shape | Members | Notes |
|---|---|---|
| Campaign CRUD | Create, Edit, GetById, GetAll (admin list) | form-shaped; validation is required-string checks only |
| Lifecycle | Activate, Complete | one-way Draft → Active → Completed |
| Code loading | ImportCodesAsync (CSV), GenerateAndImportDiscountCodesAsync (vendor) | both funnel into per-campaign `ImportOrder` sequence |
| Wave send | PreviewWaveSendAsync, SendWaveAsync, GetSendWavePageAsync | team-scoped; grant + email per eligible member |
| Delivery repair | ResendToGrantAsync, RetryAllFailedAsync | both re-enqueue through the same email factory |
| Grant reads | GetActiveOrCompletedGrantsForUserAsync, GetAllGrantsForUserAsync, GetCodeTrackingAsync | the cross-section read surface (`ICampaignServiceRead`) |
| Inbound writes | MarkGrantsRedeemedAsync (ticket sync), UpdateGrantEmailStatusAsync (outbox processor) | the cross-section writes on `ICampaignService` |
| Platform roles | IUserDataContributor (export + erase), IUserMerge (grant re-FK) | GDPR and merge fold |

Question-shapes: "manage a campaign" (CRUD + lifecycle + loading), "get codes to people"
(wave + repair), "who has what" (grant reads), "record what happened elsewhere" (inbound
writes), "platform obligations" (GDPR/merge).

## 3. Structure

The layout those shapes imply is what exists: one controller (admin pages), one service
holding all rules, one repository as sole table-toucher, a Contracts leaf carrying only
the members outsiders call, DTOs per page. No caching decorator (admin-only, cold paths).
Nothing here wants splitting; the service maps one coherent shape per question above.

## 4. Invariants

- Status walks Draft → Active → Completed, one way. Activate needs ≥1 code; Complete needs
  Active; SendWave needs Active; vendor generation needs Draft (service-enforced).
- A code is unique per campaign (DB unique `(CampaignId, Code)`); granted to at most one
  person (DB unique `CampaignCodeId` on grants); a person holds at most one grant per
  campaign (DB unique `(CampaignId, UserId)`).
- Wave allocation orders by `ImportOrder` — batch order stable and reproducible.
- Per-grant commit during wave/retry: one enqueue failure flips only that grant to Failed.
- A wave aborts before granting anything when available codes < eligible members.
- CampaignCodes mail is always-on: no opt-out check, no unsubscribe link/header.
- Template substitution (`{{Code}}`, `{{Name}}`) HTML-encodes values — done in Email's
  renderer, not here; this section forwards raw values.
- Mutations are Admin-only, with one exception: vendor-code generation is
  TicketAdminOrAdmin. TicketAdmin may also view detail; members see only their own grants.
- Redemption matching: case-insensitive, unredeemed grants on Active/Completed campaigns,
  newest campaign wins on multi-campaign code collision, N same-code redemptions consume N
  distinct grants.

## 5. Seams

None. No specified-but-unbuilt work found in the section docs, feature spec, or issue
backlog at assessment time.

## 6. Deliberately not done

- **No caching decorator** — admin-only, low volume (Campaigns.md records the decision).
- **No `ICommunicationPreferenceService` call in wave send** — CampaignCodes is always-on,
  confirmed intended (nobodies-collective/Humans#1032); don't "add the missing opt-out check".
- **No `UpdatedAt` stamp in `ReassignGrantsToUserAsync`** — the parameter exists for merge-fold
  signature parity and is deliberately discarded; the entity has no such column.
- **No service-side status guard on CSV import** — import is open in Draft and Active by
  design (post-activation top-up); only vendor generation is Draft-only.
- **No localization** — the whole UI is admin-side (`/Campaigns/Admin`), exempt; the section
  ships no resx set at all, and `_ViewImports` documents that.

## Load-bearing weirdness

- `CreatedByUserId` / `CampaignGrant.UserId` are bare Guids — no nav, no FK constraint —
  because the section assembly cannot name `User` since G5. Display names resolve through
  `IUserServiceRead`; don't reintroduce a relationship.
- The controller injects the concrete `CampaignService`, not an interface — deliberate
  (design §15 step 5): the in-section surface stays internal; only the cross-assembly
  members live on `ICampaignService`/`ICampaignServiceRead`.
- Repository is Singleton over `IDbContextFactory` (context per call) — the section's §15b
  pattern; the architecture test pins it.
- `ImportOrder` is a per-campaign monotonic int assigned at import, not an identity column —
  it exists solely to make wave allocation reproducible.
- The email body template is *markdown carrying `{{...}}` placeholders*; rendering and
  HTML-encoding happen in Email's outbox service so all mail encodes consistently — tests
  pin that raw values are forwarded.
- `EnumStringStabilityTests` lives in this test project (not the central one) because
  `CampaignStatus` is internal post-G5; renaming an enum member needs a data migration.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-29 | First doctoring: comment/doc drift cleared, contract claims aligned with code | peterdrier/Humans#1564 |
