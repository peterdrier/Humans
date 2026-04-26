<!-- freshness:triggers
  src/Humans.Application/Services/GoogleIntegration/GoogleWorkspaceUserService.cs
  src/Humans.Application/Services/GoogleIntegration/EmailProvisioningService.cs
  src/Humans.Application/Services/Users/AccountProvisioningService.cs
  src/Humans.Application/Services/Profile/UserEmailService.cs
  src/Humans.Web/Controllers/AdminController.cs
  src/Humans.Web/Controllers/EmailController.cs
  src/Humans.Web/Controllers/ProfileController.cs
  src/Humans.Infrastructure/Services/GoogleWorkspace/**
-->
<!-- freshness:flag-on-change
  Provisioning step ordering, recovery email handling, credentials email template, or admin/HumanAdmin authorization may have changed.
-->

# Workspace Account Provisioning

## Business Context

Nobodies Collective uses Google Workspace for organizational email (@nobodies.team). Admins can provision a Google Workspace account for any human from their admin page. This creates the account, generates a temporary password, links it to the human's profile, and emails the credentials to the human's personal email address so they can sign in.

## User Stories

### US-32.1: Provision @nobodies.team Account
**As an** admin or human admin
**I want to** provision a @nobodies.team email for a human
**So that** they get an organizational email account

**Acceptance Criteria:**
- Admin enters the email prefix (e.g., "alice" for alice@nobodies.team)
- Human must have first and last name in their profile
- Account must not already exist in Google Workspace
- Creates the Google Workspace account with `ChangePasswordAtNextLogin = true`
- Sets the human's personal email as the Google recovery email
- Auto-links the new email as a verified `UserEmail` with `IsNotificationTarget = true`
- Auto-sets `User.GoogleEmail` to the new address
- Audit trail recorded (`WorkspaceAccountProvisioned`)

### US-32.2: Credentials Email
**As a** human who just received a @nobodies.team account
**I want to** receive my login credentials at my personal email
**So that** I can sign in to my new account

**Acceptance Criteria:**
- Email sent to the human's personal (recovery) email, NOT the new @nobodies.team address
- Contains: username, temporary password, link to https://mail.google.com/
- Mentions that 2FA setup is required on first login
- Localized in all supported languages (en/es/de/fr/it)
- Sent via the email outbox with `triggerImmediate: true` for fast delivery

### US-32.3: Link Existing Workspace Account
**As an** admin
**I want to** link an existing @nobodies.team account to a human
**So that** orphaned workspace accounts can be associated with their human

**Acceptance Criteria:**
- Admin can search for humans by name on the @nobodies.team Accounts page
- Linking creates a verified `UserEmail` and sets `GoogleEmail` on the user
- Audit trail recorded (`WorkspaceAccountLinked`)
- No credentials email sent (user already has access)

## Provisioning Flow

```
                        ORDERING IS CRITICAL
                        See steps below — do not reorder.

┌──────────────────────────────────────────────────────────────┐
│ Step 1: Capture recovery email (personal address)           │
│         MUST happen before Step 3 changes notification      │
│         target — otherwise credentials go to the new        │
│         @nobodies.team mailbox the user can't access yet.   │
├──────────────────────────────────────────────────────────────┤
│ Step 2: Generate temp password, create Google Workspace     │
│         account (ChangePasswordAtNextLogin = true,          │
│         RecoveryEmail = personal email from Step 1)         │
├──────────────────────────────────────────────────────────────┤
│ Step 3: Link email in DB (AddVerifiedEmailAsync)            │
│         ⚠ This switches IsNotificationTarget to             │
│         @nobodies.team — GetEffectiveEmail() now returns    │
│         the new address. Step 1 MUST be complete first.     │
├──────────────────────────────────────────────────────────────┤
│ Step 4: Set User.GoogleEmail, audit log                     │
├──────────────────────────────────────────────────────────────┤
│ Step 5: Send credentials email to recovery email from       │
│         Step 1 (personal address, NOT current effective     │
│         email which is now @nobodies.team)                  │
└──────────────────────────────────────────────────────────────┘
```

## Credentials Email Content

**Subject:** Your @nobodies.team account is ready

**Body:**
- Greeting with human's display name
- Username (full @nobodies.team address)
- Temporary password
- Link to https://mail.google.com/ for login
- Note that password must be changed on first login
- Note that 2FA is required (organization policy)
- Signed by "The Humans team"

**Template keys:** `Email_WorkspaceCredentials_Subject`, `Email_WorkspaceCredentials_Body`

**Format placeholders:** `{0}` = user name, `{1}` = workspace email, `{2}` = temporary password

## Password Generation

`PasswordGenerator.GenerateTemporary()` produces a 16-character random password from:
```
ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789!@#$
```
Ambiguous characters (0, O, l, 1, I) are excluded for readability.

## Authorization

| Action | Required Role |
|--------|---------------|
| Provision account | HumanAdmin, Admin |
| Link existing account | Admin |

## Routes

| Route | Method | Action |
|-------|--------|--------|
| `/Human/{id}/Admin` | GET | Human admin page (shows provisioning form) |
| `/Human/{id}/Admin/ProvisionEmail` | POST | Provision and link @nobodies.team account |
| `/Admin/Email` | GET | List all @nobodies.team accounts, link orphans |

## Service Interfaces

### IGoogleWorkspaceUserService
```csharp
// List all @nobodies.team accounts
Task<IReadOnlyList<WorkspaceUserAccount>> ListAccountsAsync(CancellationToken ct);

// Provision a new account (creates in Google Workspace)
Task<WorkspaceUserAccount> ProvisionAccountAsync(
    string primaryEmail, string firstName, string lastName,
    string temporaryPassword, string? recoveryEmail, CancellationToken ct);

// Get an existing account (null if not found)
Task<WorkspaceUserAccount?> GetAccountAsync(string email, CancellationToken ct);

// Suspend/restore accounts
Task SuspendAccountAsync(string email, CancellationToken ct);
Task RestoreAccountAsync(string email, CancellationToken ct);
```

### IEmailService (credentials notification)
```csharp
Task SendWorkspaceCredentialsAsync(
    string recoveryEmail, string userName, string workspaceEmail,
    string tempPassword, string? culture, CancellationToken ct);
```

## Security Considerations

1. **Temporary password shown once** — displayed in admin UI success message, then lost
2. **Recovery email guards** — falls back to OAuth email if effective email is already @nobodies.team
3. **2FA enforced by org policy** — user must set up a second factor on first login
4. **Password change enforced** — `ChangePasswordAtNextLogin = true` on the Google account
5. **Credentials email to personal address only** — never sent to the @nobodies.team mailbox being created

## Related Features

- [Email Management](11-preferred-email.md) — UserEmail entity, notification target, Google service email (US-11.7 cross-references this feature)
- [Google Integration](07-google-integration.md) — Shared Drives and Groups (separate from user account provisioning)
- [Email Outbox](21-email-outbox.md) — Delivery mechanism for the credentials email
- [Audit Log](12-audit-log.md) — WorkspaceAccountProvisioned action
