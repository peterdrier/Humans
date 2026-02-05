# Membership Status

## Business Context

A member's status within the organization is computed dynamically based on multiple factors: role assignments, consent completion, and suspension state. This computed status determines access to features and team memberships.

## User Stories

### US-5.1: View My Status
**As a** member
**I want to** see my current membership status
**So that** I understand my standing in the organization

**Acceptance Criteria:**
- Status displayed prominently on profile
- Clear badge with appropriate color
- Explanation of what status means
- Actions needed to improve status (if applicable)

### US-5.2: Understand Status Requirements
**As a** member
**I want to** understand what determines my status
**So that** I can take action to become fully active

**Acceptance Criteria:**
- Shows which roles are active/expired
- Lists which consents are missing
- Indicates if suspended by admin
- Links to relevant pages for resolution

### US-5.3: Track Status Changes
**As an** administrator
**I want to** monitor members whose status has changed
**So that** I can follow up with members at risk

**Acceptance Criteria:**
- Dashboard shows members needing attention
- Filter by status type
- See history of status changes
- Export for offline processing

## Membership Status Enum

```csharp
public enum MembershipStatus
{
    None = 0,       // No active role assignments
    Active = 1,     // Has roles + all required consents
    Inactive = 2,   // Has roles but missing consents (and grace period expired)
    Suspended = 3   // Admin-suspended
}
```

## Status Computation Logic

```
┌─────────────────────────────┐
│    Compute Status           │
└──────────────┬──────────────┘
               │
        ┌──────▼──────┐
        │ Profile     │
        │ Exists?     │
        └──────┬──────┘
               │
        ┌──No──┴──Yes──┐
        │              │
   [None]       ┌──────▼──────┐
                │ IsSuspended?│
                └──────┬──────┘
                       │
                ┌─Yes──┴──No───┐
                │              │
         [Suspended]    ┌──────▼──────┐
                        │ Has Active  │
                        │ Roles?      │
                        └──────┬──────┘
                               │
                        ┌─No───┴──Yes──┐
                        │              │
                   [None]       ┌──────▼──────┐
                                │ Missing     │
                                │ Consents    │
                                │ > Grace     │
                                │ Period?     │
                                └──────┬──────┘
                                       │
                                ┌─Yes──┴──No───┐
                                │              │
                          [Inactive]      [Active]
```

### Pseudo-code
```csharp
public MembershipStatus ComputeStatus(Guid userId)
{
    var profile = GetProfile(userId);
    if (profile == null) return MembershipStatus.None;

    if (profile.IsSuspended) return MembershipStatus.Suspended;

    var hasActiveRoles = RoleAssignments
        .Any(r => r.UserId == userId
              && r.ValidFrom <= now
              && (r.ValidTo == null || r.ValidTo > now));

    if (!hasActiveRoles) return MembershipStatus.None;

    // Grace Period Logic:
    // Members only become Inactive if they have missing consents 
    // AND the grace period (e.g. 7 days) since the document update has expired.
    var hasExpiredConsents = GetMissingConsents(userId)
        .Any(v => v.EffectiveFrom + GracePeriod < now);

    return hasExpiredConsents
        ? MembershipStatus.Inactive
        : MembershipStatus.Active;
}
```

## Status Display

| Status | Badge | Color | Description |
|--------|-------|-------|-------------|
| Active | `bg-success` | Green | Full member (within grace period if updates exist) |
| Inactive | `bg-warning` | Yellow | Missing required consents past grace period |
| Suspended | `bg-danger` | Red | Admin-suspended |
| None | `bg-secondary` | Gray | No active role assignments |

## Status Dependencies

### Role Assignments
```
RoleAssignment
├── ValidFrom: Instant (when role starts)
├── ValidTo: Instant? (when role ends, null = indefinite)
└── Active if: ValidFrom <= now AND (ValidTo IS NULL OR ValidTo > now)
```

### Consent Requirements
```
For status = Active, user must have consent for:
├── All LegalDocuments where IsRequired = true AND IsActive = true
└── Specifically the latest DocumentVersion where EffectiveFrom <= now
└── Note: Users remain Active during the Grace Period following an update.
```

## Status Transitions

### Becoming Active
```
[None/Inactive] ──────▶ [Active]

Triggered by:
  - Role assignment created with ValidFrom <= now
  - All required consents submitted
  - Both conditions must be true
```

### Becoming Inactive
```
[Active] ──────▶ [Inactive]

Triggered by:
  - New document version requires re-consent AND grace period has expired
  - New required document added AND grace period has expired
```

### Becoming Suspended
```
[Any] ──────▶ [Suspended]

Triggered by:
  - Admin sets IsSuspended = true
  - Overrides all other status considerations
```

### Becoming None
```
[Active/Inactive] ──────▶ [None]

Triggered by:
  - Role assignment expires (ValidTo < now)
  - Role assignment deleted
  - No remaining active roles
```

## Impact on Features

| Status | Can Access | Team Resources (Drive/Groups) | Application |
|--------|------------|-----------------|-------------|
| Active | Full access | Full access | N/A |
| Inactive | Limited | Access Revoked | Can submit |
| Suspended | Read-only | Access Revoked | Cannot submit |
| None | Basic | No access | Can submit |

## Compliance Automation

### Inactive → Access Revocation Flow
```
┌─────────────────────┐
│ Document Updated    │
│ (Grace Period Start)│
└──────────┬──────────┘
           │
    Day 0  │
           ▼
┌─────────────────────┐
│ Send consolidated   │
│ notification email  │
└──────────┬──────────┘
           │
  Day 1-6  │ (Reminders if configured)
           ▼
┌─────────────────────┐
│ Grace Period Ends   │
└──────────┬──────────┘
           │
    Day 7  │
           ▼
┌─────────────────────┐
│ Status -> Inactive  │
│ Revoke Google Acc.  │
│ Notify User         │
└─────────────────────┘
```

### Restoration Flow
When a user with `Inactive` status signs the required documents:
1. Status is immediately re-computed as `Active`.
2. System automatically triggers `RestoreUserToAllTeamsAsync`.
3. Google Drive permissions and Group memberships are restored.
4. User receives immediate access back to all team resources.

## Monitoring & Reporting

### Dashboard Metrics
- Total members by status
- Members who became Inactive this week
- Members approaching suspension deadline
- Members unsuspended this month

### Alert Triggers
- Member status changed to Inactive
- Member suspended for non-compliance
- Large number of members affected by new document version

## Related Features

- [Authentication](01-authentication.md) - Role assignments determine status
- [Legal Documents & Consent](04-legal-documents-consent.md) - Consent completion required
- [Teams](06-teams.md) - System team membership based on status
- [Background Jobs](08-background-jobs.md) - Automated status monitoring