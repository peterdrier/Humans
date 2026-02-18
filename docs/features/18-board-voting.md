# Board Voting

## Business Context

When a human applies for Colaborador or Asociado tier, the Board votes on their application. This replaces the previous model where a single Board member could approve/reject an Asociado application. The new model requires a structured vote with individual Board member input, a meeting date, and a collective decision note. Voting happens through a dedicated dashboard that shows all pending applications and each Board member's vote.

Board voting only applies to tier applications (Colaborador and Asociado). Volunteer access is handled automatically through the consent check gate — see [Onboarding Pipeline](16-onboarding-pipeline.md).

**GDPR data minimization:** Individual Board member votes are **deleted** when the application is finalized. Only the collective decision (approve/reject), meeting date, and decision note are retained as the audit record.

## User Stories

### US-18.1: View Voting Dashboard

**As a** Board member
**I want to** see all tier applications awaiting Board decision
**So that** I can review and vote on them

**Acceptance Criteria:**
- Dashboard shows applications where consent check is Cleared and Status is Submitted or UnderReview
- Spreadsheet-style layout: rows = applications, columns = Board members
- Each cell shows the Board member's current vote (or empty if not yet voted)
- Filter by tier: Colaborador / Asociado / All
- Sort by submission date (oldest first)
- Shows applicant name, tier, motivation preview, submission date

### US-18.2: Cast Vote on Application

**As a** Board member
**I want to** cast my vote on a tier application
**So that** my position is recorded for the group decision

**Acceptance Criteria:**
- Vote options: Yay, Maybe, No, Abstain
- Can add an optional note with vote
- Can change vote before application is finalized
- Vote is timestamped
- Each Board member votes independently
- Votes are working data — deleted when application is finalized

### US-18.3: Finalize Application Decision

**As a** Board member (or Admin)
**I want to** finalize the Board's decision on a tier application
**So that** the applicant is approved or rejected

**Acceptance Criteria:**
- Any Board member can finalize (consensus model, not majority vote)
- Finalization records: decision (approve/reject), meeting date (required), decision note
- On approve:
  - Application.Status → Approved
  - Application.TermExpiresAt set (Dec 31 of appropriate odd year)
  - Profile.MembershipTier updated
  - Added to Colaboradors/Asociados system team
  - Approval notification email sent
- On reject:
  - Application.Status → Rejected
  - Decision note required (applicant deserves to know why)
  - Rejection notification email sent
  - Stays as Volunteer
- **Individual votes are deleted** after finalization (GDPR data minimization)
- Cannot finalize if no Board members have voted

### US-18.4: View Application Detail for Voting

**As a** Board member
**I want to** view the full application details before voting
**So that** I can make an informed decision

**Acceptance Criteria:**
- Shows applicant's profile (including Board-visible fields)
- Shows motivation statement and tier-specific application fields
- Shows consent check clearance date
- Shows all Board member votes cast so far (before finalization)
- Shows previous applications (if any) and their outcomes
- Shows current team memberships

## Data Model

### VoteChoice Enum
```
VoteChoice:
  Yay = 0      // In favor
  Maybe = 1    // Leaning yes but has concerns
  No = 2       // Against
  Abstain = 3  // No position
```

### BoardVote Entity (Transient — Deleted on Finalization)
```
BoardVote
├── Id: Guid
├── ApplicationId: Guid (FK → Application)
├── BoardMemberUserId: Guid (FK → User)
├── Vote: VoteChoice
├── Note: string? (4000)
├── VotedAt: Instant
└── UpdatedAt: Instant?
```

**Important:** BoardVote records are **deleted** when the application is finalized. They are working data used during the decision process, not audit records. Only the Application's DecisionNote, BoardMeetingDate, and final Status are preserved.

### Application Changes (from 15-membership-tiers.md)
```
Application (new fields)
├── MembershipTier: MembershipTier (Colaborador or Asociado)
├── TermExpiresAt: LocalDate? (set on approval)
├── BoardMeetingDate: LocalDate? (when Board finalized)
├── DecisionNote: string? (4000) (Board's collective decision note)
└── Navigation: BoardVotes (transient collection, empty after finalization)
```

### Constraints
- Unique: `(ApplicationId, BoardMemberUserId)` — one vote per Board member per application
- BoardVote.Vote stored as string (like other enums)

## Voting Dashboard Layout

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Board Voting Dashboard                                                    │
│ [Colaborador (3)]  [Asociado (1)]  [All (4)]                            │
├──────────────────────────────────────────────────────────────────────────┤
│           │ Submitted │ Tier        │ Alice  │ Bob   │ Carlos │ Action   │
│ ───────── │ ───────── │ ─────────── │ ────── │ ───── │ ────── │ ──────── │
│ Human A   │ Feb 10    │ Colaborador │ Yay    │ Yay   │  —     │ [Review] │
│ Human B   │ Feb 12    │ Asociado    │ Maybe  │  —    │ No     │ [Review] │
│ Human C   │ Feb 14    │ Colaborador │  —     │  —    │  —     │ [Review] │
│ Human D   │ Feb 15    │ Colaborador │ Yay    │ Yay   │ Yay    │ [Finalize]│
└──────────────────────────────────────────────────────────────────────────┘
```

### Vote Display

| Vote | Display | Color |
|------|---------|-------|
| Yay | Yay | Green |
| Maybe | Maybe | Yellow |
| No | No | Red |
| Abstain | Abstain | Gray |
| Not voted | — | Light gray |

## Application Review + Vote Page

```
┌──────────────────────────────────────────────────────────────┐
│ ← Back to Dashboard                                          │
├──────────────────────────────────────────────────────────────┤
│ Application: Human A — Colaborador                           │
│ Submitted: Feb 10, 2026                                      │
├──────────────────────────────────────────────────────────────┤
│ APPLICANT PROFILE                                            │
│ [Photo] Legal Name: ... │ Burner Name: ... │ Location: ...   │
│ Bio: ...                                                     │
│ Contribution Interests: ...                                  │
├──────────────────────────────────────────────────────────────┤
│ MOTIVATION                                                   │
│ "I want to become a Colaborador because..."                  │
├──────────────────────────────────────────────────────────────┤
│ BOARD VOTES (deleted after finalization)                      │
│ Alice: Yay — "Strong candidate"              (Feb 11)        │
│ Bob:   Yay — (no note)                       (Feb 12)        │
│ Carlos: — (not yet voted)                                    │
├──────────────────────────────────────────────────────────────┤
│ YOUR VOTE                                                    │
│ [Yay] [Maybe] [No] [Abstain]                                │
│ Note: [____________________________________]                 │
│ [Submit Vote]                                                │
├──────────────────────────────────────────────────────────────┤
│ FINALIZE DECISION                                            │
│ Decision: [Approve ▼]                                        │
│ Meeting Date: [____]  (required)                             │
│ Decision Note: [____________________________________]        │
│ [Finalize]                                                   │
└──────────────────────────────────────────────────────────────┘
```

## Workflow

### Voting Flow

```
Application enters Board queue
(ConsentCheckStatus = Cleared on the Profile, Application exists)
    │
    ▼
Application.Status → UnderReview (auto, when first Board member views)
    │
    ▼
Board members cast individual votes (working data)
    │
    ▼
Any Board member finalizes decision
    │
    ├── Approve
    │   ├── Application.Status → Approved
    │   ├── Application.TermExpiresAt = Dec 31 of appropriate odd year
    │   ├── Application.BoardMeetingDate = entered date
    │   ├── Application.DecisionNote = entered note
    │   ├── Profile.MembershipTier → Application's tier
    │   ├── Add to Colaboradors/Asociados system team
    │   ├── Delete all BoardVote records for this Application
    │   └── Send approval notification email
    │
    └── Reject
        ├── Application.Status → Rejected
        ├── Application.BoardMeetingDate = entered date
        ├── Application.DecisionNote = reason (required)
        ├── Delete all BoardVote records for this Application
        └── Send rejection notification email
```

## Routes

| Route | Method | Action | Access |
|-------|--------|--------|--------|
| `/OnboardingReview/BoardVoting` | GET | Voting dashboard | Board, Admin |
| `/OnboardingReview/BoardVoting/{applicationId}` | GET | Application detail + vote form | Board, Admin |
| `/OnboardingReview/BoardVoting/{applicationId}/Vote` | POST | Cast/update vote | Board |
| `/OnboardingReview/BoardVoting/{applicationId}/Finalize` | POST | Finalize decision | Board, Admin |

## Business Rules

1. **Only Board members can vote** — Admin can view and finalize but not vote
2. **One vote per Board member per application** — can update before finalization
3. **Any Board member can finalize** — consensus model, not counting votes
4. **Cannot finalize without at least one vote** — prevents empty decisions
5. **Finalization is irreversible** — once approved/rejected, the decision stands
6. **Meeting date required** — audit trail requirement
7. **Decision note required for rejection** — applicant deserves to know why
8. **Individual votes deleted on finalization** — GDPR data minimization
9. **Approval triggers immediate tier enrollment** — added to system team, term starts
10. **Terms aligned to odd-year cycles** — Dec 31 of next appropriate odd year

## Related Features

- [Membership Tiers](15-membership-tiers.md) — Tier definitions and term lifecycle
- [Onboarding Pipeline](16-onboarding-pipeline.md) — Where Board voting fits in the pipeline
- [Coordinator Roles](17-coordinator-roles.md) — Consent check gate (separate from Board voting)
- [Tier Applications](03-asociado-applications.md) — Application entity and state machine
- [Administration](09-administration.md) — Admin role and access
