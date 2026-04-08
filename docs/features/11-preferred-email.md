# Email Management

## Business Context

Members sign in using their Google account, which provides their primary email address. They may want to add additional email addresses for notifications, share specific emails on their profile with different visibility levels, or receive notifications at a non-OAuth address. The UserEmail entity supports multiple emails per user with per-email verification, visibility, and notification targeting.

## User Stories

### US-11.1: Add Email Address
**As a** member
**I want to** add additional email addresses to my account
**So that** I can use different emails for notifications or profile visibility

**Acceptance Criteria:**
- Can add any valid email address
- Must verify the email before it becomes active
- Verification email contains a secure link
- 5-minute cooldown between verification requests (rate limiting)
- Cannot claim an email already verified by another account

### US-11.2: Verify Email Address
**As a** member
**I want to** verify an email by clicking a link
**So that** the system knows I own that email address

**Acceptance Criteria:**
- Verification link works without being logged in
- Link expires based on token provider settings
- Cannot claim an email already verified by another account
- Race condition check during verification

### US-11.3: Choose Notification Target
**As a** member
**I want to** choose which email receives system notifications
**So that** I receive important messages at my preferred inbox

**Acceptance Criteria:**
- Only verified emails can be set as notification target
- Exactly one email per user must be the notification target
- OAuth email is the default notification target

### US-11.4: Set Email Visibility
**As a** member
**I want to** control which emails appear on my profile and who sees them
**So that** I can share contact info with appropriate audiences

**Acceptance Criteria:**
- Each email has independent visibility (BoardOnly, LeadsAndBoard, MyTeams, AllActiveProfiles, or hidden)
- Uses the same ContactFieldVisibility levels as ContactField
- Visibility is null by default (hidden from profile)

### US-11.5: Remove Email
**As a** member
**I want to** remove an email address I no longer use
**So that** my account stays clean

**Acceptance Criteria:**
- Cannot remove the OAuth login email
- Cannot remove the current notification target (must reassign first)
- Confirmation prompt before removal

### US-11.6: Choose Google Service Email
**As a** member
**I want to** choose which email is used for Google Groups and Drive access
**So that** I can use my @nobodies.team email for Google resources

**Acceptance Criteria:**
- Only verified emails can be selected
- If user has a verified @nobodies.team email, it's auto-selected and locked
- Default is OAuth email (GoogleEmail = null on User)
- Change takes effect on next Google sync cycle
- Profile/Emails page shows "Google Services Email" card

### US-11.7: Admin Provisions @nobodies.team Account

> **See [Workspace Account Provisioning](32-workspace-account-provisioning.md) for full details** including the provisioning flow, credentials email, ordering constraints, and 2FA requirements.

**As an** admin
**I want to** provision a @nobodies.team email from a human's admin page
**So that** I can create and link their org email in one step

**Acceptance Criteria:**
- Provisioning creates the Google Workspace account and links it to the human
- Auto-sets as notification target and Google service email
- Credentials email sent to human's personal (recovery) email with username, temp password, and login link
- Audit trail recorded

### US-11.8: Admin Links Unlinked Workspace Account
**As an** admin
**I want to** link an existing @nobodies.team account to a human
**So that** orphaned workspace accounts can be associated with their human

**Acceptance Criteria:**
- Admin can search for humans by name on the @nobodies.team Accounts page
- Linking creates a verified UserEmail and sets GoogleEmail on the user
- Audit trail recorded with WorkspaceAccountLinked action

## Data Model

### UserEmail Entity
```
UserEmail
├── Id: Guid
├── UserId: Guid (FK → User)
├── Email: string (256)
├── IsVerified: bool
├── IsOAuth: bool (cannot be deleted)
├── IsNotificationTarget: bool (exactly one per user)
├── Visibility: ContactFieldVisibility? (null = hidden)
├── VerificationSentAt: Instant? (rate limiting)
├── DisplayOrder: int
├── CreatedAt: Instant
└── UpdatedAt: Instant
```

### GoogleEmail Field on User
```
User.GoogleEmail: string? (256) — preferred email for Google services (Groups, Drive)
```

### Computed Methods on User
```csharp
GetEffectiveEmail() → returns the email marked as notification target (for system emails)
GetGoogleServiceEmail() → returns GoogleEmail ?? Email (for Google resource sync)
```

## Verification Flow

```
[User adds email]
    → Validate email format
    → Check uniqueness (among verified emails only)
    → Check rate limit (5 min cooldown)
    → Generate token via Identity
    → Create UserEmail record (IsVerified=false)
    → Send verification email

[User clicks link]
    → Validate token
    → Re-check uniqueness (race condition guard)
    → Set IsVerified=true
    → Email can now be set as notification target or given visibility
```

## Security Considerations

### Token Generation
Uses ASP.NET Identity's built-in token providers:
- `UserManager.GenerateUserTokenAsync()` with custom purpose
- `UserManager.VerifyUserTokenAsync()` for validation
- Token expiration handled by Identity configuration

### Uniqueness Enforcement
- Partial unique index: only verified emails must be unique
- Prevents claiming an email verified by another account
- Race condition check during verification

### Rate Limiting
- 5-minute cooldown between verification requests
- Tracked via `VerificationSentAt` timestamp
- Prevents email bombing

## Routes

| Route | Purpose |
|-------|---------|
| `/Profile/Emails` | Manage email addresses (add, verify, remove, set visibility, Google preference) |
| `/Profile/PreferredEmail` | Legacy redirect → `/Profile/Emails` |
| `/Admin/Email` | List all @nobodies.team accounts, provision, suspend, link to humans |
| `/Human/{id}/ProvisionEmail` | Provision @nobodies.team account from human admin page |

## Service Integration

### Google Sync
`GoogleWorkspaceSyncService` uses `GetGoogleServiceEmail()` (or `GoogleEmail ?? Email` in projections) for:
- Adding/removing users from Google Groups
- Adding/removing users from Shared Drive folders
- Drift detection for membership sync

When `GoogleEmail` differs from the OAuth email (e.g., after linking a @nobodies.team address), `AddUserToTeamResourcesAsync` proactively removes the old OAuth email from Google Groups to prevent duplicate email delivery. This means the cleanup is immediate rather than waiting for the daily reconciliation cycle.

Users with `GoogleEmailStatus == Rejected` are excluded from all sync paths (reconciliation, outbox, direct add). A 403 "Permission denied" from the Groups API — typically because the email address does not have a Google account associated with it — is treated as a permanent rejection.

### Background Jobs
These jobs use `GetEffectiveEmail()` to send to the notification target:
- `SendReConsentReminderJob` - consent reminder emails
- `SuspendNonCompliantMembersJob` - suspension notification emails

## Related Features

- [Authentication](01-authentication.md) - OAuth provides primary email
- [Profiles](02-profiles.md) - Email visibility on profile
- [Contact Fields](10-contact-fields.md) - Shares ContactFieldVisibility enum
- [Background Jobs](08-background-jobs.md) - Jobs send to effective email
