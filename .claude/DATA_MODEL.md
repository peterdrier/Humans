# Data Model

## Key Entities

| Entity | Purpose |
|--------|---------|
| User | Custom IdentityUser with Google OAuth |
| Profile | Member profile with computed MembershipStatus, MembershipTier, ConsentCheckStatus |
| UserEmail | Email addresses per user (login, verified, notifications) |
| ContactField | Contact info with per-field visibility controls |
| VolunteerHistoryEntry | Volunteer involvement history (events, roles, camps) |
| Application | Tier application (Colaborador/Asociado) with Stateless state machine |
| ApplicationStateHistory | Audit trail of Application state transitions |
| BoardVote | **Transient** individual Board member votes on Applications (deleted on finalization) |
| RoleAssignment | Temporal role memberships (ValidFrom/ValidTo) |
| LegalDocument / DocumentVersion | Legal docs synced from GitHub |
| ConsentRecord | **APPEND-ONLY** consent audit trail |
| Team / TeamMember | Working groups |
| TeamJoinRequest | Requests to join a team |
| TeamJoinRequestStateHistory | Audit trail of TeamJoinRequest state transitions |
| GoogleResource | Shared Drive folder + Group provisioning |
| AuditLogEntry | **APPEND-ONLY** system audit trail (user actions, sync ops) |

## Relationships

```
User 1──n Profile
User 1──n UserEmail
User 1──n RoleAssignment
User 1──n ConsentRecord
User 1──n TeamMember
User 1──n Application
User 1──n BoardVote (as BoardMemberUser)

Profile 1──n ContactField
Profile 1──n VolunteerHistoryEntry

Team 1──n TeamMember
Team 1──n TeamJoinRequest
Team 1──n GoogleResource
Team 1──n LegalDocument

LegalDocument 1──n DocumentVersion
DocumentVersion 1──n ConsentRecord

Application 1──n ApplicationStateHistory
Application 1──n BoardVote (transient — deleted on finalization)
TeamJoinRequest 1──n TeamJoinRequestStateHistory

AuditLogEntry n──1 User (ActorUser, optional)
AuditLogEntry n──1 GoogleResource (optional)
```

## Profile Entity

### New Properties (Onboarding Redesign)

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| MembershipTier | MembershipTier | Volunteer | Current tier — tracked on Profile, not as RoleAssignment |
| ConsentCheckStatus | ConsentCheckStatus? | null | Consent check gate status (null until all consents signed) |
| ConsentCheckAt | Instant? | null | When consent check was performed |
| ConsentCheckedByUserId | Guid? | null | Consent Coordinator who performed the check |
| ConsentCheckNotes | string? | null | Notes from the Consent Coordinator |
| RejectionReason | string? | null | Reason for rejection (when Admin rejects a flagged check) |
| RejectedAt | Instant? | null | When the profile was rejected |
| RejectedByUserId | Guid? | null | Admin who rejected the profile |

## Application Entity

Tier application entity with state machine workflow. Used for Colaborador and Asociado applications (never Volunteer). During initial signup, created inline alongside the profile. After onboarding, created via the dedicated Application route.

### Properties

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | Primary key |
| UserId | Guid | FK to User |
| MembershipTier | MembershipTier | Tier being applied for (Colaborador or Asociado) |
| Status | ApplicationStatus | Current state (Submitted, UnderReview, Approved, Rejected, Withdrawn) |
| Motivation | string (4000) | Required motivation statement |
| AdditionalInfo | string? (4000) | Optional additional information |
| Language | string? (10) | UI language at submission (ISO 639-1 code) |
| SubmittedAt | Instant | When submitted |
| UpdatedAt | Instant | Last update |
| ReviewStartedAt | Instant? | When review started |
| ResolvedAt | Instant? | When resolved (approved/rejected/withdrawn) |
| ReviewedByUserId | Guid? | Reviewer ID |
| ReviewNotes | string? (4000) | Reviewer notes / rejection reason |
| TermExpiresAt | LocalDate? | Term expiry (Dec 31 of odd year), set on approval |
| BoardMeetingDate | LocalDate? | Date of Board meeting where decision was made |
| DecisionNote | string? (4000) | Board's collective decision note (only record after vote deletion) |
| RenewalReminderSentAt | Instant? | When renewal reminder was last sent |

### Navigation Properties

- `User` — applicant
- `ReviewedByUser` — reviewer
- `StateHistory` — ApplicationStateHistory collection
- `BoardVotes` — BoardVote collection (transient, empty after finalization)

## BoardVote Entity

Individual Board member's vote on a tier application. **Transient working data** — records are deleted when the application is finalized (GDPR data minimization). Only the collective decision (Application.DecisionNote, BoardMeetingDate) is retained.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | Primary key |
| ApplicationId | Guid | FK to Application |
| BoardMemberUserId | Guid | FK to User (Board member) |
| Vote | VoteChoice | The vote choice |
| Note | string? (4000) | Optional note explaining the vote |
| VotedAt | Instant | When the vote was first cast |
| UpdatedAt | Instant? | When the vote was last updated |

**Constraint:** Unique `(ApplicationId, BoardMemberUserId)` — one vote per Board member per application.

## Enums

### MembershipTier

Membership tier indicating the level of organizational involvement.

| Value | Int | Description |
|-------|-----|-------------|
| Volunteer | 0 | Default tier, no application needed |
| Colaborador | 1 | Active contributor, requires application + Board vote, 2-year term |
| Asociado | 2 | Voting member with governance rights, requires application + Board vote, 2-year term |

Stored as string via `HasConversion<string>()`.

### ConsentCheckStatus

Status of the consent check performed by a Consent Coordinator during onboarding.

| Value | Int | Description |
|-------|-----|-------------|
| Pending | 0 | All required consents signed, awaiting Coordinator review |
| Cleared | 1 | Cleared — triggers auto-approve as Volunteer |
| Flagged | 2 | Safety concern flagged — blocks Volunteer access |

Stored as string via `HasConversion<string>()`. Nullable on Profile (null until all consents signed).

### VoteChoice

Individual Board member's vote on a tier application.

| Value | Int | Description |
|-------|-----|-------------|
| Yay | 0 | In favor |
| Maybe | 1 | Leaning yes but has concerns |
| No | 2 | Against |
| Abstain | 3 | No position |

Stored as string via `HasConversion<string>()`.

### ApplicationStatus

| Value | Int | Description |
|-------|-----|-------------|
| Submitted | 0 | Initial state |
| UnderReview | 1 | Board is reviewing/voting |
| Approved | 2 | Accepted — tier granted |
| Rejected | 3 | Denied — stays at current tier |
| Withdrawn | 4 | Applicant cancelled |

### SystemTeamType

| Value | Int | Description |
|-------|-----|-------------|
| None | 0 | User-created team |
| Volunteers | 1 | All active volunteers |
| Leads | 2 | All team leads |
| Board | 3 | Board members |
| Asociados | 4 | Approved Asociados with active terms |
| Colaboradors | 5 | Approved Colaboradors with active terms |

### AuditAction

Includes onboarding redesign actions:
- `ConsentCheckCleared` — Consent Coordinator cleared a consent check
- `ConsentCheckFlagged` — Consent Coordinator flagged a consent check
- `SignupRejected` — Admin rejected a signup
- `TierApplicationApproved` — Board approved a tier application
- `TierApplicationRejected` — Board rejected a tier application
- `TierDowngraded` — Admin downgraded a member's tier
- `MembershipsRevokedOnDeletionRequest` — GDPR deletion revoked memberships

## Constants

### RoleNames

| Constant | Value | Purpose |
|----------|-------|---------|
| Admin | "Admin" | Full system access |
| Board | "Board" | Elevated permissions, votes on tier applications |
| ConsentCoordinator | "ConsentCoordinator" | Safety gate for onboarding consent checks |
| VolunteerCoordinator | "VolunteerCoordinator" | Facilitation contact for onboarding |

### SystemTeamIds

| Constant | Value | Purpose |
|----------|-------|---------|
| Volunteers | `00000000-0000-0000-0001-000000000001` | All active volunteers |
| Leads | `00000000-0000-0000-0001-000000000002` | All team leads |
| Board | `00000000-0000-0000-0001-000000000003` | Board members |
| Asociados | `00000000-0000-0000-0001-000000000004` | Approved Asociados |
| Colaboradors | `00000000-0000-0000-0001-000000000005` | Approved Colaboradors |

## ContactField Entity

Contact fields allow members to share different types of contact information with per-field visibility controls.

### Field Types (`ContactFieldType`)

| Value | Description |
|-------|-------------|
| ~~Email~~ | **Deprecated** — use `UserEmail` entity instead. Kept for backward compatibility. |
| Phone | Phone number |
| Signal | Signal messenger |
| Telegram | Telegram messenger |
| WhatsApp | WhatsApp messenger |
| Discord | Discord username |
| Other | Custom type (requires CustomLabel) |

### Visibility Levels (`ContactFieldVisibility`)

Lower values are more restrictive. A viewer with access level X can see fields with visibility >= X.

| Value | Level | Who Can See |
|-------|-------|-------------|
| BoardOnly | 0 | Board members only |
| LeadsAndBoard | 1 | Team leads and board |
| MyTeams | 2 | Members who share a team with the owner |
| AllActiveProfiles | 3 | All active members |

### Access Level Logic

Viewer access is determined by:
1. **Self** → BoardOnly (sees everything)
2. **Board member** → BoardOnly (sees everything)
3. **Any lead** → LeadsAndBoard
4. **Shares team with owner** → MyTeams
5. **Active member** → AllActiveProfiles only

## Term Lifecycle

Colaborador and Asociado memberships have 2-year synchronized terms expiring Dec 31 of **odd years** (2027, 2029, 2031...). The `TermExpiryCalculator.ComputeTermExpiry()` method computes the expiry as the next Dec 31 of an odd year that is at least 2 years from the approval date.

- On approval: `Application.TermExpiresAt` is set
- On expiry without renewal: user reverts to Volunteer tier, removed from Colaboradors/Asociados team
- Renewal: new Application entity (same tier), goes through normal Board voting
- Reminder: `TermRenewalReminderJob` sends reminders 90 days before expiry

## Serialization Notes

- All entities use System.Text.Json serialization
- See `CODING_RULES.md` for serialization requirements
