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
└── Navigation: Profile, Applications, ConsentRecords, RoleAssignments
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

| Role | Description | Capabilities |
|------|-------------|--------------|
| **Admin** | System administrator | Full platform access, manage all features, Hangfire |
| **Board** | Board member | Approve volunteers, approve team joins, view legal names, system oversight |
| **Member** | Regular member | Access own profile, join teams, submit applications |

### ActiveMember Claim

In addition to governance roles (Admin, Board), `RoleAssignmentClaimsTransformation` adds an `ActiveMember` claim when the user is a member of the Volunteers team. This claim is the primary authorization gate for most application features.

- **Granted when**: User is in the Volunteers team (approved + all consents signed)
- **Checked by**: `MembershipRequiredFilter` (global action filter) and `_Layout.cshtml` (nav visibility)
- **Effect**: Without this claim, users can only access onboarding pages (Home, Profile, Consent, Account, Application)

See [Volunteer Status](05-volunteer-status.md) for the full onboarding pipeline and gating details.

## Security Considerations

1. **OAuth Security**: No passwords stored; relies on Google's security
2. **Session Management**: ASP.NET Core Identity handles session tokens
3. **Role Validation**: Temporal roles checked against current timestamp
4. **Membership Gating**: Global action filter restricts non-volunteers to onboarding pages
5. **Audit Trail**: RoleAssignment tracks who assigned roles and when

## Related Features

- [Profiles](02-profiles.md) - Created after authentication
- [Volunteer Status](05-volunteer-status.md) - Computed from active roles
- [Teams](06-teams.md) - Board role enables team management
