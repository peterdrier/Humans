<!-- freshness:triggers
  src/Humans.Application/Services/Auth/RoleAssignmentService.cs
  src/Humans.Application/Services/Onboarding/**
  src/Humans.Application/Services/Consent/**
  src/Humans.Web/Controllers/OnboardingReviewController.cs
  src/Humans.Web/Controllers/AdminController.cs
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Web/Authorization/MembershipRequiredFilter.cs
  src/Humans.Web/Authorization/RoleAssignmentClaimsTransformation.cs
  src/Humans.Web/Views/OnboardingReview/**
  src/Humans.Domain/Constants/RoleNames.cs
  src/Humans.Domain/Constants/RoleGroups.cs
-->
<!-- freshness:flag-on-change
  ConsentCoordinator/VolunteerCoordinator capabilities, OnboardingReview authorization, and role-management permissions — review when role names, OnboardingReviewController, or membership filter change.
-->

# Coordinator Roles

## Business Context

Two new coordinator roles add structured safety and facilitation gates to the onboarding pipeline. The **Consent Coordinator** performs safety checks on new humans before they gain Volunteer access. The **Volunteer Coordinator** serves as a facilitation contact for onboarding humans and can assist with the review process. Both roles authorize the onboarding-review tools after the user has passed the `UserState == Active` access gate.

The consent check is purely a **Volunteer-level safety gate**. It is independent of tier applications — it does not evaluate whether someone should be a Colaborador or Asociado.

These roles complement the existing Board and Admin roles. Coordinators handle day-to-day onboarding operations, freeing the Board to focus on tier application decisions.

## Roles

| Role | Purpose | Key Responsibility |
|------|---------|-------------------|
| **ConsentCoordinator** | Safety gate | Reviews consent check submissions, clears or flags new humans |
| **VolunteerCoordinator** | Facilitation | Assists with onboarding, serves as point of contact for new humans |

### Role Comparison

| Capability | Board | Admin | ConsentCoordinator | VolunteerCoordinator |
|------------|-------|-------|-------------------|---------------------|
| Requires `UserState == Active` access gate | Yes | Yes | Yes | Yes |
| Access Admin area | Yes | Yes | No | No |
| Access Onboarding Review queue | Yes | Yes | Yes | Yes (read-only) |
| Clear/Flag consent checks | Yes | Yes | Yes | No |
| Vote on tier applications | Yes | No | No | No |
| Assign roles | Yes | Yes | No | No |
| Access Hangfire | No | Yes | No | No |

## User Stories

### US-17.1: Assign Coordinator Roles

**As an** Admin or Board member
**I want to** assign Consent Coordinator or Volunteer Coordinator roles to humans
**So that** onboarding responsibilities are delegated appropriately

**Acceptance Criteria:**
- Admin can assign/end ConsentCoordinator and VolunteerCoordinator roles
- Board can assign/end ConsentCoordinator and VolunteerCoordinator roles
- Assignment uses existing RoleAssignment entity with temporal validity
- Coordinator roles appear in the available roles dropdown on HumanDetail
- Audit log records role assignment

### US-17.2: Consent Coordinator Reviews New Humans

**As a** Consent Coordinator
**I want to** review new humans who have completed their profile and consents
**So that** I can perform safety checks before they gain Volunteer access

**Acceptance Criteria:**
- See queue of humans with `ConsentCheckStatus = Pending`
- View their profile (including Board-visible fields)
- Clear: sets `ConsentCheckStatus = Cleared`; Volunteers-team provisioning follows name + consents
- Flag: sets `ConsentCheckStatus = Flagged` with required notes
- Flagged humans can be cleared later or escalated
- This check is about Volunteer safety — it does NOT evaluate tier applications

### US-17.3: Volunteer Coordinator Views Onboarding Queue

**As a** Volunteer Coordinator
**I want to** see the onboarding review queue
**So that** I can follow up with new humans and assist them

**Acceptance Criteria:**
- Can view the onboarding review queue (read-only)
- Can see profile details of humans in the queue
- Cannot clear or flag consent checks (read-only access)
- Can contact humans to help with onboarding questions

## Data Model

### RoleNames Constants (Updated)
```csharp
public static class RoleNames
{
    public const string Admin = "Admin";
    public const string Board = "Board";
    public const string ConsentCoordinator = "ConsentCoordinator";
    public const string VolunteerCoordinator = "VolunteerCoordinator";
}
```

### Role Assignment
Uses existing `RoleAssignment` entity — no new tables needed:
```
RoleAssignment
├── UserId → coordinator user
├── Role → "ConsentCoordinator" or "VolunteerCoordinator"
├── ValidFrom → start date
├── ValidTo → end date (null = indefinite)
├── AssignedByUserId → admin/board who assigned
```

## Authorization Changes

### MembershipRequiredFilter
Coordinator roles do not bypass the global access filter. A coordinator must have `UserState == Active`; after that, the role authorizes coordinator pages such as `/OnboardingReview`.

### OnboardingReview Controller Authorization
```
[Authorize(Roles = "Board,Admin,ConsentCoordinator,VolunteerCoordinator")]
```

With action-level restrictions:
- **View queue/details**: All four roles
- **Clear/Flag consent check**: Board, Admin, ConsentCoordinator only
- **Vote on tier applications**: Board only (separate Board Voting dashboard)

### AdminController Role Management
Updated `CanManageRole()` to include coordinators:
- **Admin** can assign: Admin, Board, ConsentCoordinator, VolunteerCoordinator
- **Board** can assign: Board, ConsentCoordinator, VolunteerCoordinator (not Admin)

## Onboarding Review Queue

### Access: `/OnboardingReview`

The review queue is the primary workspace for Consent Coordinators:

```
┌──────────────────────────────────────────────────────────────┐
│ Onboarding Review                                            │
├──────────────────────────────────────────────────────────────┤
│ [Pending (5)]  [Flagged (1)]  [Cleared (12)]  [All]         │
├──────────────────────────────────────────────────────────────┤
│ Photo │ Name          │ Status  │ Since    │ →               │
│ ───── │ ──────────── │ ─────── │ ──────── │                 │
│ [img] │ New Human 1  │ Pending │ Feb 15   │ →               │
│ [img] │ New Human 2  │ Pending │ Feb 14   │ →               │
│ [img] │ Flagged One  │ Flagged │ Feb 10   │ →               │
└──────────────────────────────────────────────────────────────┘
```

### Detail View

Shows the human's profile, consent status, and action buttons:

```
┌──────────────────────────────────────────────────────────────┐
│ ← Back to Queue                                              │
├──────────────────────────────────────────────────────────────┤
│ [Photo]  New Human 1                                         │
│          newhuman1@email.com                                 │
│          Profile completed: Feb 14 │ Consents signed: Feb 15 │
├──────────────────────────────────────────────────────────────┤
│ PROFILE SUMMARY                                              │
│ Legal Name: ...                                              │
│ Location: ...                                                │
│ Bio: ...                                                     │
├──────────────────────────────────────────────────────────────┤
│ CONSENT STATUS                                               │
│ ✓ Privacy Policy (v2.1) — signed Feb 15                     │
│ ✓ Code of Conduct (v1.3) — signed Feb 15                    │
├──────────────────────────────────────────────────────────────┤
│ ACTIONS                                                      │
│ Notes: [____________________________________]                │
│ [Clear ✓]  [Flag ⚠]                                        │
└──────────────────────────────────────────────────────────────┘
```

Note: The review queue does NOT show tier application information. Consent check is a Volunteer safety gate only.

## Business Rules

1. **Consent check = Volunteer safety gate** — does not evaluate tier applications
2. **ConsentCoordinator is the primary reviewer** — Board and Admin can also clear/flag as backup
3. **VolunteerCoordinator is read-only** — cannot clear or flag, only view and assist
4. **Clearing is an audit annotation** — sets `ConsentCheckStatus = Cleared` (and `IsApproved = true`) for the CC record only; it triggers no team provisioning and no access change. Volunteers admission (name + consents) is reconciled separately by `SystemTeamSyncJob`.
5. **Flagging is annotation-only** — sets `ConsentCheckStatus = Flagged`; it does NOT block Volunteer admission or app access. Reject (sets `RejectedAt`) is the kick-out lever, not Flag.
6. **Coordinators do not bypass MembershipRequiredFilter** — like everyone else, they need `UserState == Active`; the role authorizes coordinator pages only after that gate passes.
7. **Coordinator roles follow existing RoleAssignment model** — temporal, audited

## Related Features

- [Onboarding Pipeline](../onboarding/onboarding-pipeline.md) — Where coordinator roles fit in the pipeline
- [Board Voting](../governance/board-voting.md) — Board-only voting on tier applications (separate from consent check)
- [Administration](../global/administration.md) — Admin area and role management
- [Volunteer Status](../onboarding/volunteer-status.md) — MembershipRequiredFilter and access gating
