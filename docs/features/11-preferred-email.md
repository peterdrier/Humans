# Preferred Email Address

## Business Context

Members sign in using their Google account, which provides their primary email address. However, they may want system notifications (consent reminders, application status updates, suspension notices) sent to a different email address. This feature allows members to specify and verify an alternate email for receiving notifications.

## User Stories

### US-11.1: Set Preferred Email
**As a** member
**I want to** specify a different email address for system notifications
**So that** I receive important messages at my preferred inbox

**Acceptance Criteria:**
- Can enter any valid email address
- Cannot use the same email as my sign-in (OAuth) email
- Must verify the email before it becomes active
- Verification email contains a secure link
- 5-minute cooldown between verification requests (rate limiting)

### US-11.2: Verify Email Address
**As a** member
**I want to** verify my preferred email by clicking a link
**So that** the system knows I own that email address

**Acceptance Criteria:**
- Verification link works without being logged in
- Link expires based on token provider settings
- Cannot claim an email already verified by another account
- After verification, notifications are sent to preferred email

### US-11.3: View Current Settings
**As a** member
**I want to** see my current email configuration
**So that** I know where my notifications are being sent

**Acceptance Criteria:**
- Shows sign-in (OAuth) email
- Shows preferred email if set
- Shows verification status (pending/verified)
- Shows which email currently receives notifications

### US-11.4: Remove Preferred Email
**As a** member
**I want to** remove my preferred email
**So that** notifications return to my sign-in email

**Acceptance Criteria:**
- Can remove preferred email at any time
- Confirmation prompt before removal
- After removal, notifications go to sign-in email

## Data Model

### User Entity Extensions
```
User (extends IdentityUser)
├── ...existing fields...
├── PreferredEmail: string? (256)
├── PreferredEmailVerified: bool
└── PreferredEmailVerificationSentAt: Instant?
```

### Computed Property
```csharp
GetEffectiveEmail() =>
    PreferredEmailVerified && !string.IsNullOrEmpty(PreferredEmail)
        ? PreferredEmail
        : Email;
```

## Verification Flow

```
[User enters email]
    → Check not same as OAuth email
    → Check uniqueness (among verified emails only)
    → Check rate limit (5 min cooldown)
    → Generate token via Identity
    → Store pending email (Verified=false)
    → Send verification email

[User clicks link]
    → Validate token
    → Re-check uniqueness (race condition guard)
    → Set Verified=true
    → Notifications now go to preferred email
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
- Tracked via `PreferredEmailVerificationSentAt` timestamp
- Prevents email bombing

## Database Schema

```sql
-- Added to users table
ALTER TABLE users ADD COLUMN "PreferredEmail" varchar(256);
ALTER TABLE users ADD COLUMN "PreferredEmailVerified" boolean NOT NULL DEFAULT false;
ALTER TABLE users ADD COLUMN "PreferredEmailVerificationSentAt" timestamp with time zone;

-- Partial unique index (only verified emails)
CREATE UNIQUE INDEX IX_users_PreferredEmail
    ON users("PreferredEmail")
    WHERE "PreferredEmailVerified" = true AND "PreferredEmail" IS NOT NULL;
```

## UI Components

### Preferred Email Page (`/Profile/PreferredEmail`)
- Shows current email settings
- Form to enter new email address
- Pending verification status indicator
- Button to resend verification (with cooldown)
- Button to remove preferred email

### Integration with Profile Edit
- Link from Profile Edit page to Preferred Email settings

## Service Integration

### Background Jobs Updated
These jobs now use `GetEffectiveEmail()`:
- `SendReConsentReminderJob` - consent reminder emails
- `SuspendNonCompliantMembersJob` - suspension notification emails

### Email Service Interface
New method added to `IEmailService`:
```csharp
Task SendEmailVerificationAsync(
    string toEmail,
    string userName,
    string verificationUrl,
    CancellationToken cancellationToken = default);
```

## Related Features

- [Authentication](01-authentication.md) - OAuth provides primary email
- [Profiles](02-profiles.md) - Preferred email is a profile setting
- [Background Jobs](08-background-jobs.md) - Jobs send to effective email
