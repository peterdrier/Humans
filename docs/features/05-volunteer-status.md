# Volunteer Status

## Business Context

A human's volunteer status is determined by their presence in the **Volunteers team** — the system-managed team that all active volunteers belong to. Joining this team requires **consent check clearance** (which auto-sets `IsApproved`) and completion of all required legal document consents. The status is displayed on the dashboard and controls access to most application features.

Volunteer access is the **universal baseline** for all membership tiers. Whether a human selects Volunteer, Colaborador, or Asociado tier, they all become Volunteers first. Tier applications (Colaborador/Asociado) proceed in parallel through the Board voting queue and do not block Volunteer access. See [Membership Tiers](15-membership-tiers.md) and [Onboarding Pipeline](16-onboarding-pipeline.md).

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
- Filter human list by pending/active/suspended
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

Non-volunteers are restricted from accessing most of the application. A global action filter (`MembershipRequiredFilter`) enforces this by checking for the `ActiveMember` claim.

### How It Works

1. `RoleAssignmentClaimsTransformation` runs on each request and checks if the user is in the Volunteers team
2. If yes, it adds an `ActiveMember` claim to the user's identity
3. `MembershipRequiredFilter` (registered globally) checks for this claim
4. Users without the claim are redirected to the Home dashboard

### Bypass Roles

These roles bypass the MembershipRequiredFilter (have system access regardless of Volunteer status):
- **Board**
- **Admin**
- **ConsentCoordinator**
- **VolunteerCoordinator**

### Exempt Controllers

These controllers are accessible to all authenticated users regardless of volunteer status:
- **Home** — dashboard with onboarding checklist
- **Account** — account settings
- **Profile** — profile creation/editing (needed during onboarding)
- **Consent** — legal document consent (needed during onboarding)
- **Application** — tier application status view
- **OnboardingReview** — has its own role-based authorization
- **Admin** — has its own Board/Admin role gate
- **Human** — public member directory

### Navigation Gating

The main navigation hides Teams and Governance links for non-volunteers. These are only shown when the user has the `ActiveMember` claim or holds a bypass role.

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
Consent Coordinator reviews → Cleared
    │
    ▼
[Auto] IsApproved = true → Volunteers team (immediate)
    │
    ▼
ActiveMember claim granted → full app access
```

Consent check clearance triggers `SyncVolunteersMembershipForUserAsync()`, which immediately adds the user to the Volunteers team if all consents are signed. There is no waiting for a scheduled job.

The consent check is a Volunteer safety gate only — it does not evaluate tier applications.

## Migration: Existing Users

Existing approved users (`IsApproved = true`) are **grandfathered in** — they receive `ConsentCheckStatus = Cleared` in the migration and are not required to go through the consent check process. The new consent check gate only applies to humans who sign up after the feature is deployed.

## Status Dependencies

### Volunteers Team Eligibility
```
To be in the Volunteers team, a user must have ALL of:
├── Profile.IsApproved = true (set automatically by consent check clearance)
├── Profile.IsSuspended = false
├── Profile.RejectedAt = null (not rejected)
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

### Becoming Active (Pending → Active)
```
Triggered by:
  - Consent Coordinator clears consent check (sets IsApproved = true)
    AND all required Volunteers team consents are signed
  - OR: User signs final required consent
    AND consent check is already Cleared
  - Whichever happens last triggers immediate Volunteers team sync
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
1. `ConsentController.Submit` calls `SyncVolunteersMembershipForUserAsync`
2. User is immediately re-added to the Volunteers team
3. Google Drive permissions and Group memberships are restored
4. `ActiveMember` claim is granted on next request

## Related Features

- [Membership Tiers](15-membership-tiers.md) — Tier definitions (Volunteer is baseline)
- [Onboarding Pipeline](16-onboarding-pipeline.md) — Full onboarding flow
- [Coordinator Roles](17-coordinator-roles.md) — Consent Coordinator performs safety checks
- [Authentication](01-authentication.md) — ActiveMember claim, role claims
- [Legal Documents & Consent](04-legal-documents-consent.md) — Consent completion triggers team sync
- [Teams](06-teams.md) — Volunteers team is the source of truth for active status
- [Background Jobs](08-background-jobs.md) — SystemTeamSyncJob handles team membership
- [Administration](09-administration.md) — Admin human management
