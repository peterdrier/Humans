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

All roles are stored as temporal `RoleAssignment` records. Role claims are added to the user's identity via `RoleAssignmentClaimsTransformation` on each request.

### Governance Roles (manage the platform)

| Role | Description | Capabilities |
|------|-------------|--------------|
| **Admin** | System administrator | Full platform access, manage all features, Hangfire |
| **Board** | Board member | Vote on tier applications, view legal names, system oversight |

### Coordinator Roles (operational, bypass MembershipRequiredFilter)

| Role | Description |
|------|-------------|
| **ConsentCoordinator** | Safety checks on new humans (clear/flag consent checks) |
| **VolunteerCoordinator** | Read-only onboarding queue access; assists new humans |

### Section Admin Roles (scoped to one functional area)

| Role | Description |
|------|-------------|
| **HumanAdmin** | Human management pages, approve/suspend/reject humans, provision @nobodies.team accounts |
| **TeamsAdmin** | System-wide team management; view sync status but not execute |
| **CampAdmin** | Camp management and season approval |
| **TicketAdmin** | Ticket vendor integration, discount codes, ticket data export |
| **FeedbackAdmin** | View all feedback, respond to reporters, manage status |
| **FinanceAdmin** | Budget management (years, groups, categories, line items) |
| **NoInfoAdmin** | Approve/voluntell shift signups; access volunteer medical data |

### ActiveMember Claim

In addition to governance roles, `RoleAssignmentClaimsTransformation` adds an `ActiveMember` claim when the user is a member of the Volunteers team. This claim is the primary authorization gate for most application features.

- **Granted when**: User is in the Volunteers team (approved + all consents signed)
- **Checked by**: `MembershipRequiredFilter` (global action filter) and `_Layout.cshtml` (nav visibility)
- **Effect**: Without this claim, users can only access onboarding pages (Home, Profile, Consent, Account, Application)

### Authorization Policies

The app registers named authorization policies (`PolicyNames` constants in `Humans.Web.Authorization`) backed by custom `IAuthorizationHandler` implementations. These are used in views and tag helpers (`authorize-policy` attribute) alongside role-based `[Authorize(Roles = "...")]` on controllers.

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
