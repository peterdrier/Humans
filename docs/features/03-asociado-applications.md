# Tier Applications

## Business Context

The Application entity serves all tier-based membership applications: **Colaborador** (active contributor) and **Asociado** (voting member). Each Application has a `MembershipTier` field indicating which tier is being applied for. There is a specific application form for each tier — during initial signup it is embedded inline alongside profile setup for a streamlined experience, but it remains a distinct Application entity in the system.

After initial onboarding, existing volunteers apply for Colaborador or Asociado through the dedicated Application route. The inline embedding is a one-shot convenience for new signups only.

Applications go through a review workflow: after the Consent Coordinator clears the human's consent check (granting Volunteer access), the Application enters the Board's voting queue. The Board votes individually, then finalizes the decision. Approved applications set the human's tier and enroll them in the appropriate system team with a synchronized 2-year term.

**Volunteer access does not require an Application.** Only Colaborador and Asociado tiers use the Application entity. The consent check is a separate Volunteer-level safety gate. See [Onboarding Pipeline](16-onboarding-pipeline.md).

The Application entity also serves **upgrades** (Volunteer→Colaborador, Volunteer→Asociado, Colaborador→Asociado) and **renewals** (same tier, new term). Each is a new Application record.

## User Stories

### US-3.1: Inline Tier Application During Signup

**As a** new human
**I want to** apply for Colaborador or Asociado as part of my initial signup
**So that** my application is created alongside my profile in one flow

**Acceptance Criteria:**
- During initial signup, profile setup includes tier selection
- Choosing Colaborador or Asociado reveals the application form inline
- Application form collects: Motivation (required), AdditionalInfo (optional)
- Submitting creates both Profile and Application entities
- Application.MembershipTier set to the selected tier
- Application.Status = Submitted
- Language recorded from current UI locale
- Cannot create if a pending application already exists
- **This inline experience is one-shot** — not available on subsequent profile edits

### US-3.2: Apply for Tier After Onboarding

**As an** active Volunteer or Colaborador
**I want to** apply for a higher tier through the application form
**So that** I can upgrade my membership

**Acceptance Criteria:**
- Uses the dedicated Application route (not profile edit)
- Application form collects tier-specific information
- Creates a new Application entity for the target tier
- Active Volunteers can apply for Colaborador or Asociado
- Active Colaboradors can apply for Asociado
- Current tier and access maintained during review

### US-3.3: View Application Status

**As a** human with a tier application
**I want to** view my application status and history
**So that** I know where my application stands

**Acceptance Criteria:**
- Dashboard shows current application status
- Shows tier applied for (Colaborador / Asociado)
- Displays state history with timestamps
- Shows Board meeting date and decision note when finalized
- Displays term expiry date if approved

### US-3.4: Withdraw Application

**As a** human with a pending application
**I want to** withdraw my application
**So that** I can cancel if I change my mind

**Acceptance Criteria:**
- Can withdraw from Submitted or UnderReview status
- Withdrawal is recorded in state history
- Can submit new application after withdrawal

### US-3.5: Board Reviews Tier Applications

**As a** Board member
**I want to** review and vote on tier applications
**So that** qualified humans can become Colaboradors or Asociados

**Acceptance Criteria:**
- Board Voting dashboard shows pending applications
- Filter by tier (Colaborador / Asociado / All)
- View full application details and profile
- Cast individual vote (Yay / Maybe / No / Abstain)
- Finalize with meeting date and decision note
- Individual votes deleted after finalization (GDPR)
- See [Board Voting](18-board-voting.md) for full voting workflow

### US-3.6: Renew Tier

**As a** Colaborador or Asociado with an expiring term
**I want to** submit a renewal application
**So that** my tier membership continues

**Acceptance Criteria:**
- Renewal reminder shown on dashboard when term is approaching expiry
- Submitting renewal creates new Application (same tier)
- Goes through normal Board voting process
- On approval, new term starts (next 2-year cycle)
- On expiry without renewal, reverts to Volunteer

## Data Model

### Application Entity
```
Application
├── Id: Guid
├── UserId: Guid (FK → User)
├── MembershipTier: MembershipTier [Colaborador or Asociado — never Volunteer]
├── Status: ApplicationStatus [enum]
├── Motivation: string (4000) [required]
├── AdditionalInfo: string? (4000)
├── Language: string? (10) [ISO 639-1 code]
├── SubmittedAt: Instant
├── UpdatedAt: Instant
├── ReviewStartedAt: Instant?
├── ResolvedAt: Instant?
├── ReviewedByUserId: Guid?
├── ReviewNotes: string? (4000)
├── TermExpiresAt: LocalDate? [set on approval: Dec 31 of odd year]
├── BoardMeetingDate: LocalDate? [date Board finalized decision]
├── DecisionNote: string? (4000) [Board's collective decision note]
├── Navigation: StateHistory, BoardVotes (transient), User, ReviewedByUser
```

The `Language` field records the applicant's UI language at the time of submission, displayed to reviewers as the native language name to help them understand context.

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

### BoardVote Entity (Transient — Deleted on Finalization)
```
BoardVote
├── Id: Guid
├── ApplicationId: Guid (FK → Application)
├── BoardMemberUserId: Guid (FK → User)
├── Vote: VoteChoice [Yay, Maybe, No, Abstain]
├── Note: string? (4000)
├── VotedAt: Instant
└── UpdatedAt: Instant?
```

Individual votes are deleted when the application is finalized (GDPR data minimization).

### ApplicationStatus Enum
```
Submitted = 0    // Initial state, awaiting review
UnderReview = 1  // Board is reviewing/voting
Approved = 2     // Application accepted — tier granted
Rejected = 3     // Application denied — stays at current tier
Withdrawn = 4    // Applicant cancelled
```

### MembershipTier Enum
```
Volunteer = 0    // Default tier, no application needed
Colaborador = 1  // Active contributor, 2-year term
Asociado = 2     // Voting member, 2-year term
```

### VoteChoice Enum
```
Yay = 0      // In favor
Maybe = 1    // Leaning yes but has concerns
No = 2       // Against
Abstain = 3  // No position
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
| Approve | UnderReview | Approved | Board/Admin | Optional (DecisionNote) |
| Reject | UnderReview | Rejected | Board/Admin | Yes (DecisionNote required) |
| RequestMoreInfo | UnderReview | Submitted | Board/Admin | Yes |
| Withdraw | Submitted, UnderReview | Withdrawn | Applicant | No |

## Application Creation

### During Initial Signup (Inline)
```
Profile Setup → Tier selection → Application form inline → Submit
    │
    ├── Profile saved (MembershipTier = selected tier)
    └── Application created (Status = Submitted)
    │
    ▼
Both proceed through pipelines:
  - Profile → Consents → Consent Check → Volunteer access
  - Application → Board Voting → Tier enrollment (parallel)
```

### After Onboarding (Dedicated Route)
```
Active Volunteer/Colaborador → /Application/Create → Fill form → Submit
    │
    ▼
Application created → Board Voting → Approve/Reject
```

### Renewal
```
Term approaching expiry → Dashboard reminder
    │
    ▼
Submit renewal → New Application (same tier) → Board Voting
```

## Approval Effects

When an Application is approved:
1. `Application.Status` → Approved
2. `Application.TermExpiresAt` = Dec 31 of appropriate odd year
3. `Application.BoardMeetingDate` = entered meeting date
4. `Application.DecisionNote` = Board's collective note
5. `Profile.MembershipTier` → Application's tier
6. User added to Colaboradors or Asociados system team
7. Individual BoardVote records deleted
8. Approval notification email sent

## Business Rules

1. **No Application for Volunteers**: Only Colaborador and Asociado require applications
2. **Inline creation is one-shot**: Only during initial signup; afterwards use dedicated Application route
3. **Single Pending Application**: User cannot have multiple pending applications
4. **Motivation Required**: Must provide non-empty motivation statement
5. **Board Votes on Applications**: See [Board Voting](18-board-voting.md)
6. **Decision Note Required for Rejection**: Applicant deserves to know why
7. **Audit Trail**: All state changes recorded with timestamp and actor
8. **Votes Deleted on Finalization**: GDPR data minimization — only collective decision kept
9. **Terms Synchronized**: All expire Dec 31 of odd years (2027, 2029, 2031...)
10. **Upgrades and Renewals Create New Applications**: Separate Application entities

## Status Badge Styling

| Status | Badge Class | Color |
|--------|-------------|-------|
| Submitted | bg-primary | Blue |
| UnderReview | bg-info | Cyan |
| Approved | bg-success | Green |
| Rejected | bg-danger | Red |
| Withdrawn | bg-secondary | Gray |

## Volunteer Access vs Tier Application

### Volunteer Access (Consent Check → Auto-Approve)
- **Gate**: Consent Coordinator clears consent check → `IsApproved = true` → Volunteers team
- **Applies to**: All new humans — the universal onboarding gate
- **No Application needed**
- **Independent of tier applications** — consent check does not evaluate tier suitability

### Tier Application (Board Voting → Tier Enrollment)
- **Gate**: Board votes on Application → Approve/Reject
- **Applies to**: Humans who want Colaborador or Asociado — optional
- **Runs in parallel** — does not block Volunteer access

## Related Features

- [Membership Tiers](15-membership-tiers.md) — Tier definitions and lifecycle
- [Onboarding Pipeline](16-onboarding-pipeline.md) — End-to-end onboarding flow
- [Board Voting](18-board-voting.md) — Board voting process
- [Coordinator Roles](17-coordinator-roles.md) — Consent check (Volunteer gate, separate from applications)
- [Profiles](02-profiles.md) — Profile data
- [Administration](09-administration.md) — Application management
