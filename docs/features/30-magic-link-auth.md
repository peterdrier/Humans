<!-- freshness:triggers
  src/Humans.Application/Services/Auth/MagicLinkService.cs
  src/Humans.Infrastructure/Services/Auth/**
  src/Humans.Web/Controllers/AccountController.cs
  src/Humans.Web/Views/Account/**
  src/Humans.Application/Services/Profile/UserEmailService.cs
  src/Humans.Domain/Entities/User.cs
  src/Humans.Domain/Entities/UserEmail.cs
-->
<!-- freshness:flag-on-change
  Magic link token strategy (Identity vs DataProtection), email-lookup order, single-use enforcement, account-linking on OAuth, or rate limiting may have changed.
-->

# Feature 30: Magic Link Authentication

## Business Context

The platform currently supports only Google OAuth for login. This limits flexibility:

- **External contacts** (mailing list subscribers, ticket purchasers) can't be pre-provisioned as real Identity users because there's no way for them to authenticate without Google.
- **Humans without Google accounts** (or who prefer not to use Google) have no way to access the platform.
- **Future auth methods** (email/password, Apple, Microsoft) require the identity layer to support multiple credential types per user.

Magic link authentication is the foundation: a human enters their email, receives a link, clicks it, and is signed in. No password, no third-party provider. This is the simplest second auth method and unlocks the contact import use case — imported contacts are just users who haven't clicked their first magic link yet.

## Design Principles

1. **One human, many ways in.** A User can have Google OAuth, a magic link, and (future) a password — all pointing at the same identity row.
2. **Contacts are just pre-provisioned users.** A mailing list import creates a real Identity user with `EmailConfirmed = true` and no credentials. When they authenticate by any method, they claim their existing account.
3. **Work with Identity, not around it.** Use ASP.NET Identity's token providers and `SignInManager` rather than custom auth schemes, lockout hacks, or merge flows.
4. **`LastLoginAt == null` means "never authenticated."** No new enums needed to distinguish imported contacts from active humans.

## User Stories

### US-1: Login via magic link (existing user)

**As** a human who already has an account,
**I want** to log in by entering my email and clicking a link,
**So that** I don't need to use Google OAuth.

**Acceptance criteria:**
- Login page shows an "Email me a login link" option alongside the Google button
- Entering any verified email address associated with an account sends a magic link to **that specific address** (not the primary — the one they typed)
- A user with 3 verified emails can enter any of them and get a magic link sent to that address, signing in as the same human
- Entering an unknown or unverified email shows a generic "If that email exists, we've sent a link" message (no account enumeration)
- The link signs the user in and redirects to the return URL (or home)
- The link expires after 15 minutes
- The link is single-use (consumed on first click)
- If the link is expired or already used, show a clear message with a "Request new link" option

### US-2: Signup via magic link (new user)

**As** someone who wants to join the platform without Google,
**I want** to enter my email, verify it via magic link, and create an account,
**So that** I can participate without a Google account.

**Acceptance criteria:**
- If the email doesn't match any existing user, send a magic link that creates an account on click
- On first login, the user is prompted to set a display name (email is pre-filled)
- The new user follows the normal onboarding flow (profile completion, consent, etc.)
- A `UserEmail` record is created (non-OAuth, verified, notification target)

### US-3: Claim a pre-provisioned account

**As** a mailing list subscriber whose email was imported,
**I want** to click a magic link and land in my existing account,
**So that** my communication preferences and history are preserved.

**Acceptance criteria:**
- When a magic link is sent to an imported user (`LastLoginAt == null`), clicking it signs them into their existing account
- `LastLoginAt` is set on first login
- Communication preferences imported with the contact are preserved
- The user proceeds to normal onboarding (profile completion, consent)
- No merge flow needed — they're already in the system

### US-4: Link Google to an existing magic-link account

**As** a human who signed up via magic link,
**I want** to connect my Google account later,
**So that** I can use either method to log in.

**Acceptance criteria:**
- On Google OAuth callback, if no user is found by provider key but a user exists with the same verified email, link the Google login to the existing user via `AddLoginAsync`
- The user is signed in to their existing account (not a new one)
- All existing data (profile, preferences, teams) is preserved

### US-5: Google OAuth user gets magic link fallback

**As** a human who normally uses Google but can't right now,
**I want** to log in via magic link using my Google email,
**So that** I can still access the platform.

**Acceptance criteria:**
- Existing Google OAuth users can enter their email and receive a magic link
- The link signs them into their existing account
- No duplicate account is created

## Workflows

### Magic Link Login/Signup Flow

```
Human enters email on login page
  │
  ├── Email found in UserEmails (verified) → existing user
  │     └── Send magic link to THAT address (the one they typed)
  │           └── Human clicks link
  │                 ├── Token valid → sign in, update LastLoginAt, redirect
  │                 └── Token expired/used → show error + "Request new link"
  │
  ├── Email found on User.NormalizedEmail (Identity field) → existing user
  │     └── Same as above (fallback for edge cases where UserEmail row is missing)
  │
  └── Email does NOT match any User or UserEmail
        └── Send magic link with signup token (DataProtection, not Identity)
              └── Human clicks link
                    └── Create User (email confirmed, no password)
                          └── Create UserEmail (verified, notification target)
                                └── Redirect to onboarding
```

**Email lookup order:** Check `UserEmails` table first (where `IsVerified = true`), then fall back to `User.NormalizedEmail`. This ensures a user with 3 verified emails can log in with any of them.

### Google OAuth with Account Linking

```
Google OAuth callback
  │
  ├── User found by provider key → sign in (existing flow)
  │
  └── No user by provider key
        ├── User found by verified UserEmail match OR User.NormalizedEmail
        │     └── Link Google login (AddLoginAsync) → sign in
        │
        └── No user by email → create new user (existing flow)
```

**Note:** Account linking also checks `UserEmails`, not just `User.Email`. A human who signed up via magic link with email A, then added and verified email B, then does Google OAuth with email B — should still link to their existing account.

## Technical Design

### Token Strategy: Two Cases

There are two distinct cases that need different token approaches:

#### Case 1: Existing user login (Identity tokens)

When the email matches an existing user, use **ASP.NET Identity's token provider** — same pattern as existing email verification in `UserEmailService`:

```csharp
// Generate
var token = await _userManager.GenerateUserTokenAsync(
    user, TokenOptions.DefaultEmailProvider, "MagicLinkLogin");

// Verify
var isValid = await _userManager.VerifyUserTokenAsync(
    user, TokenOptions.DefaultEmailProvider, "MagicLinkLogin", token);
```

The token is bound to the user's security stamp, so it becomes invalid if the stamp rotates. **Token lifetime: 15 minutes.**

**URL format:**
```
https://{baseUrl}/Account/MagicLink?userId={userId}&token={urlEncodedToken}&returnUrl={returnUrl}
```

#### Case 2: New user signup (DataProtection tokens)

When the email doesn't match any existing user, there's no `User` row to generate an Identity token against. Use **DataProtection** instead — same pattern as the existing unsubscribe magic links in `CommunicationPreferenceService`:

```csharp
var protector = _dataProtectionProvider
    .CreateProtector("MagicLinkSignup")
    .ToTimeLimitedDataProtector();

// Generate — payload is the email address, encrypted with 15min expiry
var token = protector.Protect(email, TimeSpan.FromMinutes(15));

// Verify — returns the email, throws CryptographicException if expired/tampered
var email = protector.Unprotect(token);
```

The token is self-contained (encrypted email + expiry). No database row needed.

**URL format:**
```
https://{baseUrl}/Account/MagicLinkSignup?token={urlEncodedToken}&returnUrl={returnUrl}
```

No `userId` in the URL because the user doesn't exist yet. The encrypted email in the token is the identity claim.

### Single-Use Enforcement

**Login tokens (existing users):** After successful verification and sign-in, `SignInManager.SignInAsync` does NOT rotate the security stamp by default. Explicitly call `await _userManager.UpdateSecurityStampAsync(user)` after sign-in to invalidate the used token.

**Side effect:** This also invalidates any pending email verification tokens for that user. This is acceptable — email verification tokens are re-sent on demand via the Profile page, and the scenario (user has a pending email verification AND uses a magic link to log in within the same 15-minute window) is rare. The alternative — tracking used tokens in a separate table — is over-engineering for ~500 users.

**Signup tokens (new users):** Single-use is enforced by the fact that the callback creates the user. A second click with the same token would find the email already taken and show an appropriate message ("Account already created — use the login link instead").

### Email Lookup

The magic link request endpoint must search for emails across both tables:

```csharp
// 1. Check UserEmails first (covers all verified addresses including non-primary)
var userEmail = await _dbContext.UserEmails
    .Include(ue => ue.User)
    .FirstOrDefaultAsync(ue => ue.IsVerified &&
        ue.Email.ToLower() == email.ToLower(), ct);

if (userEmail is not null)
{
    // Generate Identity token for userEmail.User
    // Send magic link to the specific address they typed (userEmail.Email)
    return;
}

// 2. Fallback: check User.NormalizedEmail (edge case: user exists but UserEmail row missing)
var user = await _userManager.FindByEmailAsync(email);
if (user is not null)
{
    // Generate Identity token for user
    // Send magic link to user.Email
    return;
}

// 3. No match — send signup token
```

The same lookup pattern applies to the Google OAuth account linking in `ExternalLoginCallback`.

### New/Modified Files

| Layer | File | Change |
|-------|------|--------|
| Application | `Interfaces/IMagicLinkService.cs` | New interface: `SendLoginLinkAsync`, `VerifyLoginTokenAsync`, `SendSignupLinkAsync`, `VerifySignupTokenAsync` |
| Infrastructure | `Services/MagicLinkService.cs` | Token generation (Identity + DataProtection), email lookup, email sending via outbox |
| Web | `Controllers/AccountController.cs` | New actions: `MagicLinkRequest` (POST), `MagicLink` (GET, login), `MagicLinkSignup` (GET, signup), `CompleteSignup` (GET+POST). Modified: `ExternalLoginCallback` (account linking) |
| Web | `Views/Account/Login.cshtml` | Add email input + "Send login link" form alongside Google button |
| Web | `Views/Account/MagicLinkSent.cshtml` | Confirmation page ("Check your email") |
| Web | `Views/Account/MagicLinkError.cshtml` | Expired/invalid token page with "Request new link" button |
| Web | `Views/Account/CompleteSignup.cshtml` | Display name prompt for new users (email pre-filled, readonly) |
| Infrastructure | `Services/OutboxEmailService.cs` | New `SendMagicLinkAsync` method |
| Web | `Program.cs` | Configure `DataProtectionTokenProviderOptions.TokenLifespan` |
| Domain | `Entities/User.cs` | Add `MagicLinkSentAt` (nullable Instant) for rate limiting |

### Account Linking (Google OAuth)

Modify `ExternalLoginCallback` in `AccountController`:

```
Current: no user by provider key → create new user
New:     no user by provider key
           → check UserEmails for verified match
           → if found: AddLoginAsync + sign in (same user)
           → else check User.NormalizedEmail
           → if found: AddLoginAsync + sign in (same user)
           → else: create new user (existing flow)
```

This is a small change (~15 lines) in the existing callback. Wrapped in try-catch so a linking failure doesn't block the OAuth flow — falls through to create new user with a logged warning.

### Email Template

Subject: `Your login link for Nobodies`

Body (login — existing user):
```
Hi {DisplayName},

Click the link below to sign in:

{magic_link_url}

This link expires in 15 minutes and can only be used once.

If you didn't request this, you can ignore this email.
```

Body (signup — new user):
```
Welcome to Nobodies!

Click the link below to create your account:

{magic_link_url}

This link expires in 15 minutes and can only be used once.

If you didn't request this, you can ignore this email.
```

Category: `MessageCategory.System` (not opt-outable).

### Rate Limiting

To prevent abuse of the magic link endpoint:
- Track `MagicLinkSentAt` (new nullable `Instant` on `User`) — reject requests within 60 seconds of the last send
- Always show the same "If that email exists, we've sent a link" message regardless of whether the email exists (prevents account enumeration)
- Log suspicious patterns (multiple requests for different emails from same IP) but don't block at this scale

### Contact Import Implications

With magic link auth in place, importing a contact from MailerLite/TicketTailor becomes:

1. Create `User` via `UserManager.CreateAsync` — email, display name, `EmailConfirmed = true`
2. Create `UserEmail` record (verified, notification target)
3. Set `ContactSource` and `ExternalSourceId` on User (nullable fields, existing design from Aaron's PR)
4. Do NOT set lockout, do NOT create fake credentials

The user sits with `LastLoginAt == null`. When they authenticate by any method (magic link, Google, future), they claim their account. No merge, no AccountType enum, no special handling.

### What This Replaces from Aaron's PR (#209)

| Aaron's approach | This approach |
|------------------|---------------|
| `AccountType` enum (Member/Contact/Deactivated) | `LastLoginAt == null` for never-authenticated |
| `LockoutEnd = MaxValue` to prevent login | No lockout needed — they just haven't authenticated yet |
| `MergeContactToMemberAsync` | No merge — the contact IS the user |
| Auto-merge on OAuth callback | Account linking on OAuth callback (simpler) |
| `AccountMergeService` contact path | Not needed |

**What we keep from Aaron's PR:**
- `ContactSource` and `ExternalSourceId` on User (useful for tracking import origin)
- Admin Contacts UI (filtered by `LastLoginAt == null && ContactSource != null`)
- Communication preference migration (already handled by Feature 28)
- The `IContactService` concept (simplified: just creates users, no merge)

## Related Features

- **Feature 28: Communication Preferences** — contacts imported with preferences; preserved on account claim
- **Feature 29: Contact Accounts (Aaron's PR #209)** — superseded by this approach; admin UI and ContactSource fields are reusable
- **Future: Email/Password Auth** — magic link establishes the email-based auth pattern; password is an additive credential
