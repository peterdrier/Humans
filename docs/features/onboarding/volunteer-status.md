<!-- freshness:triggers
  src/Humans.Application/Services/Teams/**
  src/Humans.Application/Services/Onboarding/**
  src/Humans.Application/Services/Consent/**
  src/Humans.Application/Services/Auth/**
  src/Humans.Web/Authorization/MembershipRequiredFilter.cs
  src/Humans.Web/Authorization/RoleAssignmentClaimsTransformation.cs
  src/Humans.Web/Controllers/HomeController.cs
  src/Humans.Web/Controllers/OnboardingReviewController.cs
  src/Humans.Domain/Constants/SystemTeamIds.cs
  src/Humans.Domain/Constants/RoleNames.cs
  src/Humans.Infrastructure/Jobs/SystemTeamSyncJob.cs
-->
<!-- freshness:flag-on-change
  UserState access gate and Volunteers-team eligibility (name + consents) — review when team sync, claims transformation, or the access gate changes.
-->

# Volunteer Status

## Business Context

App access is the stored **`UserState`**: a human reaches the full application the moment `UserState == Active` — i.e. they have entered their legal name. Access does **not** derive from Volunteers-team membership. The **Volunteers team** is a system-managed Google Workspace provisioning group that named, consented volunteers belong to; `SystemTeamSyncJob` reconciles it on **name + consents** (the consent check / `IsApproved` are an audit annotation, not consulted for membership or access). Volunteer status is displayed on the dashboard.

Volunteer access is the **universal baseline** for all membership tiers. Whether a human selects Volunteer, Colaborador, or Asociado tier, they all become Volunteers first. Tier applications (Colaborador/Asociado) proceed in parallel through the Board voting queue and do not block Volunteer access. See [Membership Tiers](../governance/membership-tiers.md) and [Onboarding Pipeline](../onboarding/onboarding-pipeline.md).

The consent check is purely a **Volunteer-level safety gate** performed by a Consent Coordinator. It is independent of any tier application.

## User Stories

### US-5.1: View My Status

**As a** human
**I want to** see my current status on my dashboard
**So that** I understand my standing in the organization

**Acceptance Criteria:**
- Status displayed on dashboard
- Onboarding checklist shown to non-volunteers explaining next steps
- Quick links to Teams and Governance only visible to active volunteers
- Colaborador/Asociado badge shown if applicable (no badge for Volunteer — that's everyone)

### US-5.2: Understand Status Requirements

**As a** new human
**I want to** see what steps I need to complete to become active
**So that** I can take action

**Acceptance Criteria:**
- Dashboard shows "Getting Started" checklist:
  1. Complete profile
  2. Sign required consents
  3. Safety check (Pending / Cleared)
- Each step shows completion state
- Links to relevant pages

### US-5.3: Track Status Changes

**As an** administrator
**I want to** monitor humans whose status has changed
**So that** I can follow up with those at risk

**Acceptance Criteria:**
- Dashboard shows pending count (consent check pending)
- Filter the human list by `UserState` (bare/active/suspended/adminsuspended/rejected/deleting/merged/deleted)
- Audit trail records status-changing events

## Dashboard Status

Status is computed on the dashboard based on Volunteers team presence and profile state:

```
┌─────────────────────────────┐
│    Compute Dashboard Status │
└──────────────┬──────────────┘
               │
        ┌──────▼──────┐
        │ IsSuspended?│
        └──────┬──────┘
               │
        ┌─Yes──┴──No───┐
        │              │
   [Suspended]  ┌──────▼──────────┐
                │ Rejected?       │
                │ (RejectedAt set)│
                └──────┬──────────┘
                       │
                ┌─Yes──┴──No───┐
                │              │
           [Rejected]   ┌──────▼──────────┐
                        │ In Volunteers   │
                        │ Team?           │
                        └──────┬──────────┘
                               │
                        ┌─No───┴──Yes──┐
                        │              │
                   [Pending]    ┌──────▼──────┐
                                │ Missing     │
                                │ Consents?   │
                                └──────┬──────┘
                                       │
                                ┌─Yes──┴──No───┐
                                │              │
                           [Inactive]     [Active]
```

## Status Display

| Status | Badge | Color | Meaning |
|--------|-------|-------|---------|
| Active | `bg-success` | Green | In Volunteers team, all consents current |
| Pending | `bg-info` | Blue | Not yet in Volunteers team (awaiting consent check) |
| Inactive | `bg-warning` | Yellow | In Volunteers team but missing re-consent on updated docs |
| Suspended | `bg-danger` | Red | Admin-suspended |
| Rejected | `bg-danger` | Red | Consent check rejected by Admin |

## Volunteer Gating (MembershipRequiredFilter)

Non-Active users are restricted from most of the application. A global action filter (`MembershipRequiredFilter`) enforces this from the stored `UserState`.

### How It Works

1. `RoleAssignmentClaimsTransformation` runs on each request and stamps the user's stored `UserState` as a claim
2. `MembershipRequiredFilter` (registered globally) reads it: only `UserState == Active` reaches the app
3. Non-Active users are routed by state — `Bare` → name entry, `DeletePending` → cancel-deletion, `Suspended`/`Rejected`/`Deleted`/`Merged` → the account-status page

### Bypass

There is no role bypass. Roles authorize protected features only after `UserState == Active`; non-Active users are routed by state before role-gated controllers run.

### Exempt Controllers

Only controllers a non-Active user must still reach are exempt:
- **Account** — login/logout/OAuth
- **OnboardingWidget** — name entry (the Bare landing)
- **Profile** — profile creation/editing
- **Consent** — legal document consent
- **User** — account-status wall + cancel-deletion (the redirect targets)
- **Language** — language switching
- **Guest** — profileless account dashboard
- **GovernanceApplications**, **Feedback**, **Notifications** — any logged-in user

Role-gated app controllers are reached only after the `UserState == Active` gate passes; `[AllowAnonymous]`/API-key controllers use anonymous pass-through.

### Navigation Gating

The main navigation hides member links (City, Events, Shifts, Budget, Governance) behind the single `AppAccess` policy — shown when `UserState == Active`.

## Volunteer Onboarding Pipeline

The path to becoming an active volunteer:

```
Sign Up (Google OAuth)
    │
    ▼
Complete Profile (optional: select tier + application inline)
    │
    ▼
Sign Required Legal Documents (Volunteers team docs)
    │
    ▼
[Auto] ConsentCheckStatus → Pending
    │
    ▼
Consent Coordinator reviews → Cleared (sets IsApproved = true — audit annotation only)
    │
    ▼
Name + all consents → Volunteers team (Google Workspace), reconciled by SystemTeamSyncJob
```

Neither consent-check clearance nor consent submission triggers a per-user team sync any more — `SystemTeamSyncJob` reconciles Volunteers membership on **name + consents** (eventually consistent). App access is unaffected: it was granted at name entry (`UserState == Active`).

The consent check is a Volunteer safety gate only — it does not evaluate tier applications.

## Migration: Existing Users

Existing approved users (`IsApproved = true`) are **grandfathered in** — they receive `ConsentCheckStatus = Cleared` in the migration and are not required to go through the consent check process. The new consent check gate only applies to humans who sign up after the feature is deployed.

## Status Dependencies

### Volunteers Team Eligibility
```
To be in the Volunteers team, a user must have ALL of:
├── UserState == Active (legal name entered)
├── Not suspended / rejected / deleted / merged / delete-pending
└── All required consents for the Volunteers team signed
    └── Latest DocumentVersion where EffectiveFrom <= now for each required doc
```

### Consent Requirements (Per Team)
```
For each team the user belongs to:
├── All LegalDocuments where IsRequired = true AND IsActive = true AND TeamId matches
└── Latest version where EffectiveFrom <= now must be consented
└── Per-document GracePeriodDays before team removal on new versions
```

## Status Transitions

### Becoming Active (Bare -> Active)
```
Triggered by:
  - User enters their legal name during onboarding
  - Stored User.State becomes UserState.Active
  - App access opens immediately; Volunteers-team provisioning remains name + consents
```

### Becoming Inactive (Active → Inactive)
```
Triggered by:
  - New document version requires re-consent AND grace period has expired
  - New required document added AND grace period has expired
  - User removed from Volunteers team by system sync
```

### Becoming Suspended (Any → Suspended)
```
Triggered by:
  - Admin sets IsSuspended = true
  - Overrides all other status considerations
  - Removed from Volunteers team by system sync
```

### Becoming Rejected (Pending → Rejected)
```
Triggered by:
  - Admin rejects a flagged consent check
  - Profile.RejectionReason set, RejectedAt set
  - Human is notified, cannot become Volunteer unless rejection is reversed
```

## Compliance Automation

### Grace Period → Team Removal Flow
```
Document Updated (new version published)
    │
Day 0: Notification email sent to affected team members
    │
Day 1-6: Reminder emails (configurable per document)
    │
Day N (GracePeriodDays expires, default 7):
    │
    ▼
System sync removes user from team
    → Google resource access revoked
    → Audit entry logged
```

### Restoration Flow
When a user signs the missing documents:
1. `ConsentController.Submit` writes the `ConsentRecord` (it no longer triggers a per-user team sync)
2. `SystemTeamSyncJob` re-adds the user to the Volunteers team on its next run (name + consents)
3. Google Drive permissions and Group memberships are restored
4. App access is unaffected throughout — it follows `UserState == Active`, not Volunteers membership

## Related Features

- [Membership Tiers](../governance/membership-tiers.md) — Tier definitions (Volunteer is baseline)
- [Onboarding Pipeline](../onboarding/onboarding-pipeline.md) — Full onboarding flow
- [Coordinator Roles](../shifts/coordinator-roles.md) — Consent Coordinator performs safety checks
- [Authentication](../auth/authentication.md) — UserState access claim, role claims
- [Legal Documents & Consent](../legal-and-consent/legal-documents-consent.md) — Consent completion contributes to Volunteers eligibility (name + consents)
- [Teams](../teams/teams.md) — Volunteers team is a Google Workspace provisioning group (name + consents), not the access gate
- [Background Jobs](../global/background-jobs.md) — SystemTeamSyncJob handles team membership
- [Administration](../global/administration.md) — Admin human management
