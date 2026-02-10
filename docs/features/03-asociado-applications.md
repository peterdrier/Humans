# Asociado Applications

## Business Context

Volunteers who want to become **Asociados** (voting members with governance rights) submit a formal application. This is an optional upgrade for ~20% of volunteers — it is not part of the standard volunteer onboarding. Applications include a motivation statement and go through a review workflow managed by Board members. The process maintains a complete audit trail for governance compliance.

## User Stories

### US-3.1: Submit Asociado Application
**As a** volunteer
**I want to** apply to become an Asociado (voting member)
**So that** I can participate in governance, assemblies, and elections

**Acceptance Criteria:**
- Can submit application with motivation statement
- Optional additional information field
- Must confirm accuracy of information
- Cannot submit if pending application exists
- Receives confirmation of submission

### US-3.2: View Application Status
**As an** applicant
**I want to** view my application status and history
**So that** I know where my application stands

**Acceptance Criteria:**
- Shows current status with visual badge
- Displays full state history with timestamps
- Shows reviewer name and notes when applicable
- Displays submission date and resolution date

### US-3.3: Withdraw Application
**As an** applicant
**I want to** withdraw my pending application
**So that** I can cancel if I change my mind

**Acceptance Criteria:**
- Can withdraw from Submitted or UnderReview status
- Withdrawal is recorded in state history
- Can submit new application after withdrawal

### US-3.4: Review Applications (Board)
**As a** Board member
**I want to** review and process Asociado applications
**So that** qualified volunteers can become voting members

**Acceptance Criteria:**
- View list of pending applications
- Filter by status
- Start review (moves to UnderReview)
- Approve with optional notes
- Reject with required reason
- Request more information (returns to Submitted)

## Data Model

### Application Entity
```
Application
├── Id: Guid
├── UserId: Guid (FK → User)
├── Status: ApplicationStatus [enum]
├── Motivation: string (4000) [required]
├── AdditionalInfo: string? (4000)
├── Language: string? (10) [ISO 639-1 code, e.g. "es", "en"]
├── SubmittedAt: Instant
├── UpdatedAt: Instant
├── ReviewStartedAt: Instant?
├── ResolvedAt: Instant?
├── ReviewedByUserId: Guid?
├── ReviewNotes: string? (4000)
└── Navigation: StateHistory
```

The `Language` field records the applicant's UI language at the time of submission. This is displayed to reviewers on the application detail page (shown as the native language name, e.g. "Castellano", "Deutsch") to help them understand which language the applicant was working in.

### ApplicationStateHistory Entity
```
ApplicationStateHistory
├── Id: Guid
├── ApplicationId: Guid (FK → Application)
├── Status: ApplicationStatus
├── ChangedAt: Instant
├── ChangedByUserId: Guid (FK → User)
└── Notes: string? (4000)
```

### ApplicationStatus Enum
```
Submitted = 0    // Initial state, awaiting review
UnderReview = 1  // Admin has started reviewing
Approved = 2     // Application accepted
Rejected = 3     // Application denied
Withdrawn = 4    // Applicant cancelled
```

## State Machine

```
                    ┌─────────────┐
                    │  Submitted  │
                    └──────┬──────┘
                           │
          ┌────────────────┼────────────────┐
          │                │                │
    ┌─────▼─────┐    ┌─────▼─────┐    ┌─────▼─────┐
    │  Withdraw │    │ StartReview│    │           │
    └─────┬─────┘    └─────┬─────┘    │           │
          │                │          │           │
    ┌─────▼─────┐    ┌─────▼─────┐    │           │
    │ Withdrawn │    │UnderReview│    │           │
    └───────────┘    └─────┬─────┘    │           │
                           │          │           │
          ┌────────┬───────┼───────┬──┘           │
          │        │       │       │              │
    ┌─────▼────┐ ┌─▼───┐ ┌─▼────┐ ┌▼──────────┐  │
    │ Approve  │ │Reject│ │Withdraw│RequestInfo│  │
    └─────┬────┘ └──┬──┘ └───┬───┘ └─────┬─────┘  │
          │         │        │           │        │
    ┌─────▼────┐ ┌──▼────┐ ┌─▼───────┐   │        │
    │ Approved │ │Rejected│ │Withdrawn│   └────────┘
    └──────────┘ └────────┘ └─────────┘   (back to Submitted)
```

### State Transitions

| Trigger | From | To | Actor | Notes Required |
|---------|------|-----|-------|----------------|
| StartReview | Submitted | UnderReview | Board/Admin | No |
| Approve | UnderReview | Approved | Board/Admin | Optional |
| Reject | UnderReview | Rejected | Board/Admin | Yes |
| RequestMoreInfo | UnderReview | Submitted | Board/Admin | Yes |
| Withdraw | Submitted, UnderReview | Withdrawn | Applicant | No |

## Application Workflow

### Applicant Flow
```
┌──────────────┐
│  Create      │
│  Application │
└──────┬───────┘
       │
┌──────▼───────┐
│  Fill Form   │
│  - Motivation│
│  - Info      │
└──────┬───────┘
       │
┌──────▼───────┐
│  Confirm     │
│  Accuracy    │
└──────┬───────┘
       │
┌──────▼───────┐
│   Submit     │
└──────┬───────┘
       │
┌──────▼───────┐
│  Wait for    │──────────▶ [Optional: Withdraw]
│  Review      │
└──────┬───────┘
       │
┌──────▼───────┐
│  Notification│
│  of Result   │
└──────────────┘
```

### Admin Review Flow
```
┌──────────────┐
│  View Queue  │
│  (Pending)   │
└──────┬───────┘
       │
┌──────▼───────┐
│  Select      │
│  Application │
└──────┬───────┘
       │
┌──────▼───────┐
│ Start Review │
└──────┬───────┘
       │
┌──────▼───────┐
│  Review      │
│  Details     │
└──────┬───────┘
       │
   ┌───┴───┐
   │       │
┌──▼──┐ ┌──▼──┐ ┌───────────┐
│Approve│ │Reject│ │Request Info│
└───────┘ └──┬──┘ └─────┬─────┘
              │         │
         [Reason]  [What needed]
```

## Business Rules

1. **Volunteers Only**: Only active volunteers (in Volunteers team) can apply
2. **Single Pending Application**: User cannot have multiple pending applications
3. **Motivation Required**: Must provide non-empty motivation statement
4. **Accuracy Confirmation**: Must explicitly confirm information accuracy
5. **Rejection Reason**: Board must provide reason when rejecting
6. **Info Request Notes**: Board must specify what information is needed
7. **Audit Trail**: All state changes recorded with timestamp and actor

## Status Badge Styling

| Status | Badge Class | Color |
|--------|-------------|-------|
| Submitted | bg-primary | Blue |
| UnderReview | bg-info | Cyan |
| Approved | bg-success | Green |
| Rejected | bg-danger | Red |
| Withdrawn | bg-secondary | Gray |

## Volunteer Acceptance vs Asociado Application

The system has two distinct approval processes:

### Volunteer Acceptance (Profile.IsApproved)
- **Purpose**: Gate for basic volunteer enrollment (Volunteers team, Google Workspace access)
- **Process**: Board member sets `IsApproved = true` on the member's profile
- **Applies to**: All new users - required before `SystemTeamSyncJob` enrolls them
- **Complexity**: Simple approve/reject decision on the person

### Asociado Application (Application entity)
- **Purpose**: Formal governance/voting membership in the association
- **Process**: Motivation statement, state machine workflow (Submitted -> UnderReview -> Approved/Rejected)
- **Applies to**: Volunteers who want to participate in governance - explicitly optional
- **Complexity**: Detailed review with notes, state history, and audit trail

The Application view explicitly states: "This is optional. Most volunteers don't need to become asociados."

## Related Features

- [Authentication](01-authentication.md) - Must be logged in to apply
- [Profiles](02-profiles.md) - Profile data used in review
- [Administration](09-administration.md) - Admin application management
