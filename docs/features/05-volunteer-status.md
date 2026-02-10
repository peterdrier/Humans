# Volunteer Status

## Business Context

A user's volunteer status is determined by their presence in the **Volunteers team** — the system-managed team that all active volunteers belong to. Joining this team requires both Board approval (`Profile.IsApproved`) and completion of all required legal document consents. The status is displayed on the dashboard and controls access to most application features.

This is about being a **volunteer** (~100% of users). It has nothing to do with becoming an Asociado (voting member), which is a separate, optional process. See [Asociado Applications](03-asociado-applications.md).

## User Stories

### US-5.1: View My Status
**As a** user
**I want to** see my current volunteer status on my dashboard
**So that** I understand my standing in the organization

**Acceptance Criteria:**
- Status displayed prominently on dashboard with colored badge
- Onboarding checklist shown to non-volunteers explaining next steps
- Quick links to Teams and Governance only visible to active volunteers

### US-5.2: Understand Status Requirements
**As a** new user
**I want to** see what steps I need to complete to become an active volunteer
**So that** I can take action

**Acceptance Criteria:**
- Dashboard shows "Getting Started" checklist: profile, consents, board approval
- Each step shows completion state
- Links to relevant pages for each step

### US-5.3: Track Status Changes
**As an** administrator
**I want to** monitor volunteers whose status has changed
**So that** I can follow up with those at risk

**Acceptance Criteria:**
- Dashboard shows pending volunteer count
- Filter volunteer list by pending/active/suspended
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
| Pending | `bg-info` | Blue | Not yet in Volunteers team (awaiting approval and/or consents) |
| Inactive | `bg-warning` | Yellow | In Volunteers team but missing re-consent on updated docs |
| Suspended | `bg-danger` | Red | Admin-suspended |

## Volunteer Gating (MembershipRequiredFilter)

Non-volunteers are restricted from accessing most of the application. A global action filter (`MembershipRequiredFilter`) enforces this by checking for the `ActiveMember` claim.

### How It Works

1. `RoleAssignmentClaimsTransformation` runs on each request and checks if the user is in the Volunteers team
2. If yes, it adds an `ActiveMember` claim to the user's identity
3. `MembershipRequiredFilter` (registered globally) checks for this claim
4. Users without the claim are redirected to the Home dashboard

### Exempt Controllers

These controllers are accessible to all authenticated users regardless of volunteer status:
- **Home** — dashboard with onboarding checklist
- **Account** — account settings
- **Profile** — profile creation/editing (needed during onboarding)
- **Consent** — legal document consent (needed during onboarding)
- **Application** — Asociado application (optional, but accessible)
- **Admin** — has its own Board/Admin role gate
- **Human** — public member directory

### Navigation Gating

The main navigation hides Teams and Governance links for non-volunteers. These are only shown when the user has the `ActiveMember` claim or holds an Admin/Board role.

## Volunteer Onboarding Pipeline

The path to becoming an active volunteer:

```
Sign Up (Google OAuth)
    │
    ▼
Complete Profile
    │
    ▼
Sign Required Legal Documents (Volunteers team docs)
    │
    ▼
Wait for Board Approval (Profile.IsApproved = true)
    │
    ▼
System adds to Volunteers team (immediate, via SyncVolunteersMembershipForUserAsync)
    │
    ▼
ActiveMember claim granted → full app access
```

Both approval and consent completion trigger an immediate check via `SystemTeamSyncJob.SyncVolunteersMembershipForUserAsync()`. Whichever happens last causes the user to be added to the Volunteers team. There is no waiting for a scheduled job.

## Status Dependencies

### Volunteers Team Eligibility (What Makes a Volunteer)
```
To be in the Volunteers team, a user must have ALL of:
├── Profile.IsApproved = true (Board has approved them)
├── Profile.IsSuspended = false
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
  - Board approves volunteer (sets IsApproved = true)
    AND all required Volunteers team consents are signed
  - OR: User signs final required consent
    AND Profile.IsApproved is already true
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

- [Authentication](01-authentication.md) - ActiveMember claim, role claims
- [Legal Documents & Consent](04-legal-documents-consent.md) - Consent completion triggers team sync
- [Teams](06-teams.md) - Volunteers team is the source of truth for active status
- [Background Jobs](08-background-jobs.md) - SystemTeamSyncJob handles team membership
- [Administration](09-administration.md) - Volunteer approval, Sync System Teams action