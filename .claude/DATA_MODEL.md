# Data Model

## Contents

- [Key Entities](#key-entities) | [Relationships](#relationships) | [User Entity](#user-entity) | [Profile Entity](#profile-entity)
- [Application Entity](#application-entity) | [BoardVote Entity](#boardvote-entity) | [EmailOutboxMessage Entity](#emailoutboxmessage-entity)
- [Campaign](#campaign-entity) | [CampaignCode](#campaigncode-entity) | [CampaignGrant](#campaigngrant-entity) | [SystemSetting](#systemsetting-entity) | [FeedbackReport](#feedbackreport-entity)
- [CommunicationPreference Entity](#communicationpreference-entity)
- [Enums](#enums): MembershipTier, ConsentCheckStatus, VoteChoice, ApplicationStatus, SystemTeamType, AuditAction, MessageCategory, RotaPeriod, RolePeriod
- [Constants](#constants) | [ContactField Entity](#contactfield-entity) | [Term Lifecycle](#term-lifecycle) | [Camp Enums](#camp-enums)

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
| TeamRoleDefinition | Named role slots on a team (name, description, slot count, priorities, IsManagement flag, Period) |
| TeamRoleAssignment | Assigns a team member to a specific slot in a role definition |
| AuditLogEntry | **APPEND-ONLY** system audit trail (user actions, sync ops) |
| Camp | Camp core entity (contact, slug, flags) |
| CampSeason | Per-year season data (name, blurbs, community info, placement) |
| CampLead | Lead assignments with Primary/CoLead roles |
| CampImage | Image metadata (files stored on disk) |
| CampHistoricalName | Name history for tracking renames |
| CampSettings | Singleton settings (public year, open seasons) |
| EmailOutboxMessage | Queued/sent/failed transactional email records |
| Campaign | Bulk code distribution campaign |
| CampaignCode | Individual code belonging to a campaign |
| CampaignGrant | Assignment of a code to a user |
| SystemSetting | Key/value store for runtime configuration (e.g., outbox pause flag) |
| TicketOrder | Ticket purchase order synced from vendor (one per purchase) |
| TicketAttendee | Individual ticket holder (issued ticket, multiple per order) |
| TicketSyncState | Singleton tracking ticket sync operational state |
| EventSettings | Singleton event config — dates, timezone, EE capacity, caps |
| Rota | Shift container — belongs to department + event, with Period (Build/Event/Strike) and optional PracticalInfo |
| Shift | Single work slot — DayOffset + StartTime + Duration + IsAllDay flag |
| ShiftSignup | Links User to Shift with state machine (Pending/Confirmed/Refused/Bailed/Cancelled/NoShow), optional SignupBlockId for range signups |
| GeneralAvailability | Per-user per-event day availability (AvailableDayOffsets stored as jsonb) |
| VolunteerEventProfile | Per-event volunteer profile with skills, dietary, medical data |
| FeedbackReport | In-app feedback from users (bug reports, feature requests, questions) with screenshot support |

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
Team 1──n TeamRoleDefinition
Team 1──n Team (ParentTeam → ChildTeams, self-referencing)
TeamRoleDefinition 1──n TeamRoleAssignment
TeamMember 1──n TeamRoleAssignment

Team 1──n LegalDocument

LegalDocument 1──n DocumentVersion
DocumentVersion 1──n ConsentRecord

Application 1──n ApplicationStateHistory
Application 1──n BoardVote (transient — deleted on finalization)
TeamJoinRequest 1──n TeamJoinRequestStateHistory

AuditLogEntry n──1 User (ActorUser, optional)
AuditLogEntry n──1 GoogleResource (optional)

Camp 1──n CampSeason
Camp 1──n CampLead
Camp 1──n CampImage
Camp 1──n CampHistoricalName
Camp n──1 User (CreatedByUser)
CampLead n──1 User
CampSeason n──1 User (ReviewedByUser, optional)

Campaign 1──n CampaignCode
Campaign 1──n CampaignGrant
Campaign n──1 User (CreatedByUser)
CampaignCode 1──1 CampaignGrant (once assigned)
CampaignGrant n──1 User
CampaignGrant 1──n EmailOutboxMessage

EmailOutboxMessage n──1 User (optional)
EmailOutboxMessage n──1 CampaignGrant (optional)

TicketOrder 1──n TicketAttendee
TicketOrder n──1 User (MatchedUser, optional — auto-matched by email)
TicketAttendee n──1 User (MatchedUser, optional — auto-matched by email)

EventSettings 1──n Rota
Rota n──1 Team (department — ParentTeamId IS NULL)
Rota 1──n Shift
Shift 1──n ShiftSignup
ShiftSignup n──1 User (volunteer)
ShiftSignup n──1 User (EnrolledByUser, optional)
ShiftSignup n──1 User (ReviewedByUser, optional)
EmailOutboxMessage n──1 ShiftSignup (optional)
GeneralAvailability n──1 User
GeneralAvailability n──1 EventSettings
VolunteerEventProfile n──1 User
VolunteerEventProfile n──1 EventSettings
```

## User Entity

### Google Email Preference

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| GoogleEmail | string? (256) | null | Preferred email for Google services (Groups, Drive). Auto-set to @nobodies.team when provisioned/linked. Falls back to OAuth email when null. |

Methods:
- `GetGoogleServiceEmail()` → `GoogleEmail ?? Email` (for Google resource sync)
- `GetEffectiveEmail()` → notification target email or OAuth email (for system notifications)

### Contact Import Properties

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| ContactSource | ContactSource? | null | Where imported from (Manual, MailerLite, TicketTailor); null for self-registered users |
| ExternalSourceId | string?(256) | null | ID in the external source system |

A contact is identified by `ContactSource != null && LastLoginAt == null`. When a contact authenticates, `LastLoginAt` is set and they become a regular user.

### Campaign-Related Properties

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| UnsubscribedFromCampaigns | bool | false | Set via /Unsubscribe/{token}; excludes user from future campaign sends |

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
| Status | ApplicationStatus | Current state (Submitted, Approved, Rejected, Withdrawn) |
| Motivation | string (4000) | Required motivation statement |
| AdditionalInfo | string? (4000) | Optional additional information |
| Language | string? (10) | UI language at submission (ISO 639-1 code) |
| SubmittedAt | Instant | When submitted |
| UpdatedAt | Instant | Last update |
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

## EmailOutboxMessage Entity

Stores all transactional emails queued for delivery. Processed by `ProcessEmailOutboxJob`; cleaned up by `CleanupEmailOutboxJob`.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | Primary key |
| RecipientEmail | string | Delivery address |
| RecipientName | string? | Display name |
| Subject | string | Email subject line |
| HtmlBody | string | Rendered HTML body |
| PlainTextBody | string? | Optional plain-text alternative |
| TemplateName | string | Template identifier used to render this message |
| UserId | Guid? | FK to User (optional) |
| CampaignGrantId | Guid? | FK to CampaignGrant (optional) |
| ReplyTo | string? | Reply-To header value |
| ExtraHeaders | string? | JSON-encoded additional headers (e.g., List-Unsubscribe) |
| Status | EmailOutboxStatus | Queued / Sent / Failed |
| CreatedAt | Instant | When queued |
| PickedUpAt | Instant? | When first picked up by the job |
| SentAt | Instant? | When successfully delivered |
| RetryCount | int | Number of delivery attempts |
| LastError | string? | Last delivery error message |
| NextRetryAt | Instant? | Earliest time for next retry attempt |

### EmailOutboxStatus

| Value | Description |
|-------|-------------|
| Queued | Awaiting delivery |
| Sent | Successfully delivered |
| Failed | Exhausted all retries |

Stored as int.

## Campaign Entity

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | Primary key |
| Title | string | Campaign display name |
| Description | string? | Optional description |
| EmailSubject | string | Subject line template |
| EmailBodyTemplate | string | Liquid/Razor body template |
| Status | CampaignStatus | Draft / Active / Completed |
| CreatedAt | Instant | When created |
| CreatedByUserId | Guid | FK to User |

### CampaignStatus

| Value | Description |
|-------|-------------|
| Draft | Codes can be imported; sending not yet active |
| Active | Sending waves is enabled |
| Completed | Campaign closed |

Stored as int.

## CampaignCode Entity

One row per individual code belonging to a campaign. Codes are imported in bulk; each is assigned to at most one user via a CampaignGrant.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | Primary key |
| CampaignId | Guid | FK to Campaign |
| Code | string | The code value (unique per campaign) |
| ImportedAt | Instant | When imported |

## CampaignGrant Entity

Records the assignment of a specific code to a specific user.

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | Primary key |
| CampaignId | Guid | FK to Campaign |
| CampaignCodeId | Guid | FK to CampaignCode (unique — one grant per code) |
| UserId | Guid | FK to User |
| AssignedAt | Instant | When assigned |
| LatestEmailStatus | EmailOutboxStatus? | Status of most recent delivery attempt |
| LatestEmailAt | Instant? | Timestamp of most recent delivery attempt |

## SystemSetting Entity

Key/value store for runtime configuration flags. Currently used for:

| Key | Purpose |
|-----|---------|
| `email_outbox_paused` | When `"true"`, `ProcessEmailOutboxJob` skips processing |

| Property | Type | Purpose |
|----------|------|---------|
| Key | string | Primary key |
| Value | string | Setting value |

## FeedbackReport Entity

In-app feedback submitted by authenticated users. Admins triage via the admin UI; Claude Code can manage via the REST API (`/api/feedback`).

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | Primary key |
| UserId | Guid | FK → User (reporter) |
| Category | FeedbackCategory | Bug, FeatureRequest, Question |
| Description | string | Feedback text (max 5000) |
| PageUrl | string | URL where feedback was submitted |
| UserAgent | string? | Browser user agent string |
| ScreenshotFileName | string? | Original filename |
| ScreenshotStoragePath | string? | Relative path under wwwroot/uploads/feedback/ |
| ScreenshotContentType | string? | MIME type (image/jpeg, image/png, image/webp) |
| Status | FeedbackStatus | Open, Acknowledged, Resolved, WontFix |
| AdminNotes | string? | Internal notes (max 5000) |
| GitHubIssueNumber | int? | Linked GitHub issue |
| AdminResponseSentAt | Instant? | When last response email was sent |
| CreatedAt | Instant | Submission timestamp |
| UpdatedAt | Instant | Last modification |
| ResolvedAt | Instant? | When resolved/won't-fix |
| ResolvedByUserId | Guid? | FK → User (resolver, SetNull on delete) |

**Table:** `feedback_reports`
**Indexes:** Status, CreatedAt, UserId

## CommunicationPreference Entity

Per-user, per-category email opt-in/opt-out preferences. One row per user per category. Used for CAN-SPAM/GDPR compliance.

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → User (Cascade) |
| Category | MessageCategory | Enum stored as string |
| OptedOut | bool | true = user opted out |
| UpdatedAt | Instant | Last change |
| UpdateSource | string (100) | "Profile", "MagicLink", "OneClick", "Default", "DataMigration" |

**Table:** `communication_preferences`
**Unique constraint:** `(UserId, Category)`
**Indexes:** UserId

Defaults are created lazily: System=on, EventOperations=on, CommunityUpdates=off, Marketing=off.

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

### MessageCategory

Categories of system communications for preference management.

| Value | Int | Description |
|-------|-----|-------------|
| System | 0 | Critical account/consent/security notifications. Always on. |
| EventOperations | 1 | Shift changes, schedule updates, team additions. Default: on. |
| CommunityUpdates | 2 | Community news, facilitated messages. Default: off. |
| Marketing | 3 | Campaign emails, promotions. Default: off. |

Stored as string via `HasConversion<string>()`.

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
| Submitted | 0 | Initial state, awaiting Board vote |
| Approved | 2 | Accepted — tier granted |
| Rejected | 3 | Denied — stays at current tier |
| Withdrawn | 4 | Applicant cancelled |

### SystemTeamType

| Value | Int | Description |
|-------|-----|-------------|
| None | 0 | User-created team |
| Volunteers | 1 | All active volunteers |
| Coordinators | 2 | All team coordinators |
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
- `FeedbackResponseSent` — Admin sent an email response to a feedback report

### FeedbackCategory

| Value | Description |
|-------|-------------|
| Bug | Bug report |
| FeatureRequest | Feature request |
| Question | General question |

### FeedbackStatus

| Value | Description |
|-------|-------------|
| Open | New, unreviewed |
| Acknowledged | Admin has seen it |
| Resolved | Fixed or addressed |
| WontFix | Will not be addressed |

### RotaPeriod

Explicit period set on a Rota by the coordinator. Drives creation UX (all-day vs time-slotted) and signup UX (date-range vs individual). Distinct from computed `ShiftPeriod`.

| Value | Int | Description |
|-------|-----|-------------|
| Build | 0 | Build period — all-day shifts, date-range signup |
| Event | 1 | Event period — time-slotted shifts, individual signup |
| Strike | 2 | Strike period — all-day shifts, date-range signup |

Stored as string via `HasConversion<string>()`.

### RolePeriod

Period tag on a TeamRoleDefinition indicating when the role is active. Used for roster page filtering.

| Value | Int | Description |
|-------|-----|-------------|
| YearRound | 0 | Active year-round |
| Build | 1 | Active during build period |
| Event | 2 | Active during event period |
| Strike | 3 | Active during strike period |

Stored as string via `HasConversion<string>()`.

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
| Coordinators | `00000000-0000-0000-0001-000000000002` | All team coordinators |
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
| CoordinatorsAndBoard | 1 | Team coordinators and board |
| MyTeams | 2 | Members who share a team with the owner |
| AllActiveProfiles | 3 | All active members |

### Access Level Logic

Viewer access is determined by:
1. **Self** → BoardOnly (sees everything)
2. **Board member** → BoardOnly (sees everything)
3. **Any coordinator** → CoordinatorsAndBoard
4. **Shares team with owner** → MyTeams
5. **Active member** → AllActiveProfiles only

## Term Lifecycle

Colaborador and Asociado memberships have 2-year synchronized terms expiring Dec 31 of **odd years** (2027, 2029, 2031...). The `TermExpiryCalculator.ComputeTermExpiry()` method computes the expiry as the next Dec 31 of an odd year that is at least 2 years from the approval date.

- On approval: `Application.TermExpiresAt` is set
- On expiry without renewal: user reverts to Volunteer tier, removed from Colaboradors/Asociados team
- Renewal: new Application entity (same tier), goes through normal Board voting
- Reminder: `TermRenewalReminderJob` sends reminders 90 days before expiry

## Camp Enums

| Enum | Values |
|------|--------|
| CampSeasonStatus | Pending, Active, Full, Rejected, Withdrawn |
| CampLeadRole | Primary, CoLead |
| CampVibe | Adult, ChillOut, ElectronicMusic, Games, Queer, Sober, Lecture, LiveMusic, Wellness, Workshop |
| CampNameSource | Manual, NameChange |
| YesNoMaybe | Yes, No, Maybe |
| KidsVisitingPolicy | Yes, DaytimeOnly, No |
| PerformanceSpaceStatus | Yes, No, WorkingOnIt |
| AdultPlayspacePolicy | Yes, No, NightOnly |
| SpaceSize | Sqm150, Sqm300, Sqm450, Sqm600, Sqm750, Sqm900, Sqm1200, Sqm1500, Sqm2000, Sqm2400, Sqm2800 |
| SoundZone | Blue, Green, Yellow, Orange, Red, Surprise |
| ElectricalGrid | Yellow, Red, Norg, OwnSupply, Unknown |

All stored as strings via `HasConversion<string>()`. `Vibes` stored as jsonb array.

## Serialization Notes

- All entities use System.Text.Json serialization
- See `CODING_RULES.md` for serialization requirements
