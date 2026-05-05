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
  Provisioning step ordering, recovery email handling, credentials email template, admin/HumanAdmin authorization, or post-provisioning account management (2FA enrollment status, recovery-email visibility, password reset, combined Reset+2FA recovery flow on `/Google/Accounts`) may have changed.
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

### US-32.4: Surface 2FA Enrollment State
**As an** admin
**I want to** see which @nobodies.team accounts have completed 2-Step Verification enrollment
**So that** I can spot broken accounts before the human reports being locked out

**Acceptance Criteria:**
- `/Google/Accounts` table shows a per-row 2FA chip: green "2FA ready" (enrolled) or red "2FA not set up" (unenrolled)
- Summary counter at the top: "X accounts missing 2FA setup" — counts active unenrolled accounts only (suspended accounts are excluded since they can't sign in anyway)
- Alert banner + client-side filter toggle to show only the unenrolled set
- Backed by `WorkspaceUserAccount.IsEnrolledIn2Sv`, sourced from the Directory API `users.get` `isEnrolledIn2Sv` field

### US-32.5: Surface Recovery Email
**As an** admin
**I want to** see the personal recovery email Google has on file for each account
**So that** I can validate the recovery channel before the human is locked out

**Acceptance Criteria:**
- `/Google/Accounts` table shows a "Recovery Email" column per row
- Accounts with no recovery email show a yellow "None" badge with explanatory tooltip
- Backed by `WorkspaceUserAccount.RecoveryEmail`, sourced from the Directory API `users.get` `recoveryEmail` field

### US-32.6: Reset Password (with one-shot delivery)
**As an** admin
**I want to** reset the password for a @nobodies.team account and see the new temporary password once
**So that** I can hand it to a human who's lost access

**Acceptance Criteria:**
- "Reset Password" button per row, Admin-only, CSRF-protected, hidden for suspended accounts
- Confirm-before-post explains the password will be shown once
- Calls Google Admin SDK `users.update` with `password` + `changePasswordAtNextLogin: true`
- New temp password appears once in a modal with copy-to-clipboard (clipboard format `pw: <password>`) and an "I've delivered these securely" acknowledgement gate
- Modal text-content is cleared on close so browser back/refresh can't re-expose the password (TempData single-use)
- Audit trail recorded (`WorkspaceAccountPasswordReset`)

### US-32.7: Reset Password + Get 2FA (combined recovery)
**As an** admin
**I want to** reset the password AND grab one fresh backup verification code in a single action
**So that** I can unblock a locked-out human regardless of their 2SV-enrollment state — they can sign in with the password + code, then enroll/re-enroll a real second factor

**Acceptance Criteria:**
- "Reset + 2FA" button per row, Admin-only, CSRF-protected, hidden for suspended accounts
- Confirm-before-post explains both will be shown once
- Calls `IGoogleAdminService.ResetPasswordAndGenerate2FaAsync` which composes the existing password-reset + backup-codes flows; only the **first** code from the freshly-issued set is surfaced (the rest are unused — Google rotates the whole set destructively, but the admin only ever needs one)
- Confirmed (2026-05-04) to work for accounts that have not yet completed 2SV enrollment
- Both credentials appear once in the same modal as the password-only flow, with copy-to-clipboard formatted as:
  ```
  pw: <temp password>
  2fa: <single backup code>
  ```
- Modal text-content is cleared on close so browser back/refresh can't re-expose the secrets
- Two audit entries recorded: `WorkspaceAccountPasswordReset` then `WorkspaceAccountBackupCodesGenerated`
- Requires the `admin.directory.user.security` scope on the service account credential

## Account-Management Invariants

### Combined-recovery composition + partial-success handling
`ResetPasswordAndGenerate2FaAsync` calls the underlying password-reset and backup-code-generation in sequence:

1. **Password-reset failure short-circuits** — if `ResetPasswordAsync` fails, the method returns `Success: false` and never attempts the backup-code call (no point handing out codes for an account the human still can't sign into).
2. **Partial success on backup-code failure** — if the password reset succeeded but Google rejects the backup-code request, the method still returns `Success: true` with the temp password and `BackupCode: null`. Google rotates passwords destructively just like codes, so dropping the password to retry would lock the human out further. The admin sees "Password reset for X. Backup-code generation failed — deliver the password only and ask the human to re-attempt the 2FA request out-of-band."

### Backup-codes audit-and-delivery ordering
`GenerateBackupCodesAsync` (used standalone by the combined flow) writes its audit entry **after** Google has rotated the codes and before returning them, with two safety guards:

1. **Empty-list guard** — if Google's `Generate` succeeds but the subsequent `List` returns 0 codes, no audit entry is written and the method returns `Success: false`. We never record "generated 0 codes" in the audit log.
2. **Audit-failure preservation** — if the audit `LogAsync` throws after Google has issued the new set, the codes are still returned (audit failure is logged at `LogCritical` for out-of-band reconciliation). Google has already invalidated the previously-issued set, so dropping the new codes would lock the human out — account recovery is real-time, audit reconciliation is operational.

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
| View `/Google/Accounts` (2FA status, recovery email, etc.) | Admin |
| Reset password (one-shot delivery) | Admin |
| Reset password + get 2FA backup code (combined recovery) | Admin |

## Routes

| Route | Method | Action |
|-------|--------|--------|
| `/Human/{id}/Admin` | GET | Human admin page (shows provisioning form) |
| `/Human/{id}/Admin/ProvisionEmail` | POST | Provision and link @nobodies.team account |
| `/Google/Accounts` | GET | List all @nobodies.team accounts with 2FA status, recovery email, link orphans (replaces the legacy `/Admin/Email` route) |
| `/Google/Accounts/ResetPassword` | POST | Reset password — temp password shown once in a modal |
| `/Google/Accounts/ResetPasswordAndGenerate2Fa` | POST | Reset password + issue one backup verification code — both shown once in the same modal |

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
Task ReactivateAccountAsync(string email, CancellationToken ct);

// Reset password (admin-initiated)
Task ResetPasswordAsync(string email, string newPassword, CancellationToken ct);

// Backup verification codes (post-provisioning recovery surface)
// Combined recovery composes ResetPasswordAsync + GenerateBackupCodesAsync
// at the IGoogleAdminService layer (ResetPasswordAndGenerate2FaAsync).
Task<IReadOnlyList<string>> GenerateBackupCodesAsync(string email, CancellationToken ct);
```

`WorkspaceUserAccount` carries the post-provisioning visibility fields used by the admin surface: `IsEnrolledIn2Sv` (from Directory API `isEnrolledIn2Sv`) and `RecoveryEmail` (from `recoveryEmail`).

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
