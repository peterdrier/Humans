# Camps — Section Invariants

## Concepts

- A **Camp** (also called "Barrio") is a themed community camp. Each camp has a unique URL slug, one or more leads, and optional images.
- A **Camp Season** is a per-year registration for a camp, containing the year-specific name, description, community info, and placement details.
- A **Camp Lead** is a human responsible for managing a camp. Leads have a role: Primary or CoLead.
- A **Camp Member** is a human who belongs to a camp for a specific season. Membership is **post-hoc** — humans join through the camp's own process (website, spreadsheet, WhatsApp) first, then tell Humans about it. Per-season, not per-camp; no carry-forward across years. Private: never rendered on anonymous or public views.
- **Camp Settings** is a singleton controlling which year is public (shown in the directory) and which seasons accept new registrations.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Anyone (including anonymous) | Browse the camps directory, view camp details and season details |
| Any authenticated human | Register a new camp (which creates a new season in Pending status). Request to join a camp for the open season. Withdraw own pending request. Leave own active membership. |
| Camp lead | Edit their camp's details, manage season registrations, manage co-leads, upload/manage images, manage historical names. Approve/reject pending member requests. Remove active members. View the camp member list. |
| CampAdmin, Admin | All camp lead capabilities on all camps. Approve/reject season registrations. Manage camp settings (public year, open seasons, name lock dates). View withdrawn and rejected seasons. Export camp data |
| Admin | Delete camps |

## Invariants

- Each camp has a unique slug used for URL routing.
- Camp season status follows: Pending then Active, Full, Rejected, or Withdrawn. Only CampAdmin can approve or reject a season.
- Only camp leads or CampAdmin can edit a camp.
- Camp images are stored on disk; metadata and display order are tracked per camp.
- Historical names are recorded when a camp is renamed.
- Camp settings control which year is shown publicly and which seasons accept registrations.
- Camp membership is per-season. A human can only have one active or pending `CampMember` row per season (enforced by a partial unique index on `(CampSeasonId, UserId) WHERE Status <> 'Removed'`). Removed rows are retained for audit and allow re-requesting.
- A camp is "open for membership" for the current public year only when its season for that year is Active or Full. Pending/Rejected/Withdrawn seasons do not accept membership requests.
- Camp member lists are never rendered on anonymous or public views.
- Camp role assignments require the assignee to be an Active `CampMember` of the same season (auto-promoted if not).
- Camp role-definition CRUD is CampAdmin/Admin-only; camp leads can only fill / empty slots.
- Same human cannot hold two slots of the same role in the same season; same human can hold slots across multiple distinct roles.
- Role assignments are hidden from anonymous visitors (camp detail page renders no role section).
- Deactivating a role definition preserves historical assignments but hides it from new-assignment UI and from the compliance report.
- Cascades: removing a `CampMember` deletes all their role assignments for that season; transitioning a season to Rejected or Withdrawn deletes all role assignments for that season.

## Negative Access Rules

- Regular humans **cannot** edit camps they do not lead.
- Camp leads **cannot** approve or reject season registrations — that requires CampAdmin or Admin.
- CampAdmin **cannot** delete camps. Only Admin can delete a camp.
- Anonymous visitors **cannot** register camps, edit any camp data, request to join a camp, or see a camp's member list.
- A human **cannot** withdraw another human's pending membership request or leave another human's active membership.

## Triggers

- When a camp is registered, its initial season is created with Pending status.
- Season approval or rejection is performed by CampAdmin.
- When a camp season transitions to `Rejected` or `Withdrawn`, all pending `CampMember` rows for that season are auto-withdrawn (status → Removed).
- When a camp membership request is approved or rejected, the requester receives an in-app notification (`CampMembershipApproved` / `CampMembershipRejected`).

## Cross-Section Dependencies

- **Profiles**: Camp leads are linked to humans. Lead assignment requires a valid human account.
- **Admin**: Camp settings management is restricted to CampAdmin and Admin.

## Architecture — Current vs Target

See `.claude/DESIGN_RULES.md` for the full rules.

**Owning services:** `CampService`, `CampContactService`
**Owned tables:** `camps`, `camp_seasons`, `camp_leads`, `camp_members`, `camp_images`, `camp_historical_names`, `camp_settings`

### Current Violations

**Controllers:** Compliant — no DbContext injection.
**Services:** Compliant — CampService and CampContactService only query their own tables.
**Cache:** Compliant — CampService owns its camp-related caches.

**Incoming violations (other services querying Camp-owned tables):**
- `ProfileService` queries `CampLeads` directly
- `CityPlanningService` queries `CampSeasons` directly

### Target State

- Already compliant internally. Incoming violations are tracked in the Profiles and City Planning section docs.
