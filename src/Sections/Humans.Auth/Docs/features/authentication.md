<!-- freshness:triggers
  src/Sections/Humans.Auth/**
  src/Sections/Humans.Users/Services/**
  src/Humans.Web/Authorization/**
  src/Humans.Interfaces/Authorization/**
  src/Humans.Web/Controllers/AccountController.cs
  src/Sections/Humans.Development/Controllers/DevLoginController.cs
  src/Sections/Humans.Users.Contracts/User.cs
  src/Sections/Humans.Auth/Domain/RoleAssignment.cs
  src/Humans.Interfaces/Constants/RoleNames.cs
  src/Humans.Interfaces/Constants/RoleGroups.cs
-->
<!-- freshness:flag-on-change
  Authentication flow, role-claims transformation, role catalog, MembershipRequiredFilter, and policy names — review when auth/identity surfaces change.
-->

# User Authentication & Accounts

## Business Context

Nobodies Collective requires a secure, streamlined authentication system that integrates with Google Workspace, as all active members have organizational Google accounts. The system must support temporal role tracking for governance compliance.

## User Stories

### US-1.1: Google Sign-In
**As a** prospective or existing member
**I want to** sign in using my Google account
**So that** I don't need to manage another set of credentials

**Acceptance Criteria:**
- User clicks "Sign in with Google" button
- Redirected to Google OAuth consent screen
- Upon approval, user is authenticated
- If first-time user, account is automatically created
- User's Google profile picture and display name are imported

### US-1.2: Automatic Profile Creation
**As a** new user signing in for the first time
**I want to** have my account automatically created
**So that** I can immediately start using the platform

**Acceptance Criteria:**
- New users are created with Google profile data
- Display name defaults to Google display name
- Profile picture URL is captured
- Preferred language defaults to English
- CreatedAt timestamp is recorded

### US-1.3: Role Assignment Tracking
**As an** administrator
**I want to** assign roles with validity periods
**So that** I can track historical role memberships (e.g., annual board terms)

**Acceptance Criteria:**
- Roles have ValidFrom and ValidTo dates
- Expired roles are retained for historical audit
- Active roles are computed based on current date
- Notes field for documenting assignment reason

## Data Model

### User Entity
```
User (extends IdentityUser<Guid>)
├── DisplayName: string (256)
├── PreferredLanguage: string (10) [default: "en"]
├── ProfilePictureUrl: string? (2048)
├── CreatedAt: Instant
├── LastLoginAt: Instant?
├── MagicLinkSentAt: Instant?
└── Navigation: UserEmails (required for Email override), EventParticipations
```

### RoleAssignment Entity
```
RoleAssignment
├── Id: Guid
├── UserId: Guid (FK → User)
├── RoleName: string (256) ["Admin", "Board", etc.]
├── ValidFrom: Instant
├── ValidTo: Instant?
├── Notes: string? (2000)
├── CreatedAt: Instant
└── CreatedByUserId: Guid (FK → User)
```

## Authentication Flow

```
┌──────────┐     ┌─────────────┐     ┌────────────┐
│  User    │────▶│ Login Page  │────▶│   Google   │
└──────────┘     └─────────────┘     │   OAuth    │
                                     └─────┬──────┘
                                           │
                 ┌─────────────┐           │
                 │  Callback   │◀──────────┘
                 │  Handler    │
                 └──────┬──────┘
                        │
          ┌─────────────┴─────────────┐
          │                           │
    ┌─────▼─────┐              ┌──────▼──────┐
    │ Existing  │              │  New User   │
    │   User    │              │  Creation   │
    └─────┬─────┘              └──────┬──────┘
          │                           │
          └───────────┬───────────────┘
                      │
               ┌──────▼──────┐
               │  Update     │
               │ LastLoginAt │
               └──────┬──────┘
                      │
               ┌──────▼──────┐
               │  Dashboard  │
               └─────────────┘
```

## Authorization Roles

All roles are stored as temporal `RoleAssignment` records. Role claims are added to the user's identity via `RoleAssignmentClaimsTransformation` on each request.

### Governance Roles (manage the platform)

| Role | Description | Capabilities |
|------|-------------|--------------|
| **Admin** | System administrator | Full platform access, manage all features, Hangfire |
| **Board** | Board member | Vote on tier applications, view legal names, system oversight |

### Coordinator Roles (operational)

| Role | Description |
|------|-------------|
| **ConsentCoordinator** | Safety checks on new humans (clear/flag consent checks) |
| **VolunteerCoordinator** | Read-only onboarding queue access; assists new humans |

### Section Admin Roles (scoped to one functional area)

| Role | Description |
|------|-------------|
| **HumanAdmin** | Human management pages, suspend/reject humans, provision @nobodies.team accounts |
| **TeamsAdmin** | System-wide team management; view sync status but not execute |
| **CampAdmin** | Camp management and season approval |
| **TicketAdmin** | Ticket vendor integration, discount codes, ticket data export |
| **FeedbackAdmin** | None since #977 — Feedback is retired and Admin-only. Assignable and still shown on the Staff page, but grants no access |
| **FinanceAdmin** | Budget management (years, groups, categories, line items) |
| **StoreAdmin** | Store-domain superset: catalog, orders, payments, invoices, treasury sync (FinanceAdmin retains parallel access for accounting workflows) |
| **EventsAdmin** | Approve, reject, and request edits on event guide submissions |
| **CantinaAdmin** | Read-only cantina roster for meal planning; dietary preferences only, never medical conditions |
| **EETeamAdmin** | Cross-team Early-Entry administrator — grant/edit/revoke early-entry on any team with `EarlyEntryEnabled`; confers nothing else |
| **NoInfoAdmin** | Approve/voluntell shift signups; access volunteer medical data |

### UserState Access Claim

In addition to governance roles, `RoleAssignmentClaimsTransformation` stamps the user's stored `UserState` as a claim. `UserState == Active` — the user has entered their legal name — is the single source of truth for app access. It does **not** derive from Volunteers-team membership.

- **Granted when**: `UserState == Active` (legal name entered)
- **Checked by**: `MembershipRequiredFilter` (global action filter, routes non-Active users by state) and the `AppAccess` policy used for `_Layout.cshtml` nav visibility
- **Escape**: none; roles do not bypass the stored `UserState` access gate
- **Effect**: non-Active users are routed by state (Bare → name entry, DeletePending → cancel-deletion, Suspended/AdminSuspended/Rejected/Deleted/Merged → account-status page)

### Authorization Policies

The app registers named authorization policies (`PolicyNames` constants in `Humans.UI.Authorization`) backed by custom `IAuthorizationHandler` implementations. These are used in views and tag helpers (`authorize-policy` attribute) alongside role-based `[Authorize(Roles = "...")]` on controllers.

### Onboarding Name Gate

A second global action filter, `NameRequiredFilter`, runs strictly after authentication. Any authenticated user whose profile has no real `BurnerName` (a Stub profile, or an Active profile with blank required names) is redirected to the burner + legal-name form at `OnboardingWidget/Names` before they can reach the rest of the app. It is the single gate covering OAuth/Google first sign-in, imported contacts hitting the magic-link `ExistingUser` branch, and legacy blank-`BurnerName` accounts (nobodies-collective/Humans#812). It only ever redirects — it **never blocks sign-in** — and keys on the cache-backed `UserInfo.HasRequiredNameFields`, so the gate opens on the next request once names are saved. The `Account` and `Language` controllers, plus `OnboardingWidget/Names`, `Home/Error`, and `Home/Privacy`, are exempt.

See [Volunteer Status](../../../Humans.Onboarding/Docs/features/volunteer-status.md) for the full onboarding pipeline and gating details.

## Security Considerations

1. **OAuth Security**: No passwords stored; relies on Google's security
2. **Session Management**: Every sign-in path (Google OAuth, magic link, gate terminal, dev login) signs in with `isPersistent: true`; the Identity application cookie carries a 14-day sliding lifetime (`ExpireTimeSpan` + `SlidingExpiration` in `Program.cs`) — any visit inside the window extends it, 14 days away requires re-login
3. **Role Validation**: Temporal roles checked against current timestamp
4. **Access Gating**: Global action filter routes non-Active users by `UserState`
5. **Audit Trail**: RoleAssignment tracks who assigned roles and when

## Related Features

- [Profiles](../../../Humans.Users/Docs/features/profiles.md) - Created after authentication
- [Volunteer Status](../../../Humans.Onboarding/Docs/features/volunteer-status.md) - UserState access and Volunteers-team provisioning
- [Teams](../../../Humans.Teams/Docs/features/Teams-feature.md) - Board role enables team management
