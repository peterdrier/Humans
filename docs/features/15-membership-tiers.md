# Membership Tiers

## Business Context

Nobodies Collective has four effective membership tiers, though only three are managed within this system. **All humans start as Volunteers** — the default tier with immediate access after consent check clearance. Humans who want deeper involvement can apply for **Colaborador** status (active contributor) or **Asociado** status (voting member per Spanish nonprofit statutes). **Board** membership is a fourth tier managed through an external process — the system only tracks their role assignments.

Tier is tracked on the `Profile` entity (`MembershipTier` field), not as a role. Tier applications use the existing `Application` entity (extended with a `MembershipTier` field). During initial signup, the application form is embedded inline alongside the profile setup for a streamlined one-shot experience. After initial onboarding, tier applications go through the dedicated Application route.

### Three Managed Tiers

| Tier | Description | Application Required | Board Vote | Term |
|------|-------------|---------------------|------------|------|
| **Volunteer** | Default. Full app access, team participation. | No | No (consent check only) | Indefinite |
| **Colaborador** | Active contributor with project/event responsibilities. | Yes | Yes | 2-year cycle (Dec 31, odd years) |
| **Asociado** | Voting member with governance rights (assemblies, elections). | Yes | Yes | 2-year cycle (Dec 31, odd years) |

### System Teams (Separate, Not Nested)

Each tier has its own system team. Asociados are **not** in the Colaboradors team — they are separate memberships:

| Team | Members |
|------|---------|
| Volunteers | All active humans (every tier) |
| Colaboradors | Only humans with approved Colaborador applications |
| Asociados | Only humans with approved Asociado applications |
| Board | Board members (managed via RoleAssignment, external process) |

### No Volunteer Badge

Volunteer is the default tier for everyone. No badge is displayed for Volunteers on the dashboard — badges are only shown for Colaborador and Asociado.

## User Stories

### US-15.1: Inline Tier Application During Signup

**As a** new human signing up
**I want to** apply for Colaborador or Asociado as part of my initial profile setup
**So that** I don't have to go through a separate application process

**Acceptance Criteria:**
- During initial signup, profile setup includes a tier selection step
- Choosing Colaborador or Asociado reveals the application form inline
- Submitting creates both a Profile and an Application entity behind the scenes
- This is a **one-shot** experience — after initial onboarding, profile edit has NO tier/application sections
- Default is Volunteer (no application form shown)

### US-15.2: Apply for Tier After Onboarding

**As an** active Volunteer
**I want to** apply for Colaborador or Asociado status
**So that** I can increase my involvement

**As an** active Colaborador
**I want to** apply for Asociado status
**So that** I can participate in governance

**Acceptance Criteria:**
- Uses the dedicated Application route (not profile edit)
- Application form collects tier-specific information (motivation, etc.)
- Creates a new Application entity for the target tier
- User retains current tier/access while application is pending
- On approval, tier is upgraded; on rejection, stays at current tier

### US-15.3: View Tier Status

**As a** human
**I want to** see my current tier and any pending application
**So that** I understand my standing

**Acceptance Criteria:**
- Dashboard shows Colaborador/Asociado badge if applicable (no badge for Volunteer)
- If a tier application is pending, shows status
- If application was rejected, shows notification with reason
- Term expiry date shown for Colaboradors/Asociados

### US-15.4: Admin Tier Downgrade

**As an** administrator
**I want to** downgrade a human's tier
**So that** tier changes can be managed when needed

**Acceptance Criteria:**
- Admin-initiated only (not self-service)
- Removes from higher-tier team (Colaboradors or Asociados)
- Audit log entry created
- Human retains Volunteer access

## Data Model

### MembershipTier Enum
```
MembershipTier:
  Volunteer = 0    // Default tier, no application needed
  Colaborador = 1  // Active contributor, requires application + Board vote
  Asociado = 2     // Voting member, requires application + Board vote
```

### Profile Changes
```
Profile (existing entity — new field)
├── MembershipTier: MembershipTier (default Volunteer)
```

Tier is stored on Profile, **not** as a RoleAssignment. Roles (Admin, Board, ConsentCoordinator, VolunteerCoordinator) are tracked separately via RoleAssignment.

### Application Changes
```
Application (existing entity — new fields)
├── MembershipTier: MembershipTier (Colaborador or Asociado — never Volunteer)
├── TermExpiresAt: LocalDate? (set on approval: Dec 31 of current/next odd year)
├── BoardMeetingDate: LocalDate? (when Board finalized decision)
├── DecisionNote: string? (4000) (Board's collective note)
```

## Term Lifecycle

### Synchronized 2-Year Cycles

Terms are synchronized to fixed 2-year cycles ending Dec 31 of **odd years**:
- Cycle 1: Jan 1, 2026 → Dec 31, 2027
- Cycle 2: Jan 1, 2028 → Dec 31, 2029
- Cycle 3: Jan 1, 2030 → Dec 31, 2031

All Colaborador and Asociado terms within a cycle expire on the same date. If approved mid-cycle, the first term is shorter (expires at the current cycle end). Renewals grant the next full 2-year cycle.

### Term Expiry Calculation

`TermExpiresAt` = Dec 31 of the next odd year that is at least 2 years from the approval date. Computed by `TermExpiryCalculator.ComputeTermExpiry()`.

Examples:
- Approved March 2026 → 2026+2=2028 (even) → Dec 31, 2029
- Approved June 2027 → 2027+2=2029 (odd) → Dec 31, 2029
- Approved January 2028 → 2028+2=2030 (even) → Dec 31, 2031

### Renewal

Renewal is a new Application entity (same tier). Goes through normal Board voting. Approved renewal grants the next 2-year cycle.

### Lapse

If term expires without renewal, human reverts to Volunteer tier and is removed from the Colaboradors/Asociados system team.

## Application Flow

### During Initial Signup (Inline)
```
Profile Setup → Tier selection → Colaborador/Asociado form inline → Submit
    │
    ├── Profile created (MembershipTier = selected tier)
    └── Application created (MembershipTier = Colaborador or Asociado)
    │
    ▼
Both proceed through their respective pipelines:
  - Profile → Consents → Consent Check → Volunteer access
  - Application → Board Voting → Tier enrollment (parallel)
```

### After Onboarding (Dedicated Application Route)
```
Active Volunteer/Colaborador → /Application → Fill form → Submit
    │
    ▼
Application created → Board Voting → Approve/Reject
    │
    ├── Approved → Profile.MembershipTier updated, added to tier system team
    └── Rejected → stays at current tier
```

## Business Rules

1. **Everyone starts as Volunteer** — tier selection is optional
2. **Volunteer doesn't require an Application** — consent check clearance is sufficient
3. **Inline application is one-shot** — only during initial signup, never on subsequent profile edits
4. **After onboarding, applications use the dedicated route** — profile edit is just profile data
5. **Asociados are not in Colaboradors team** — separate system teams
6. **One pending Application per user** — cannot have multiple active tier applications
7. **Downgrades are admin-only** — no self-service downgrade
8. **Terms are synchronized** — all expire Dec 31 of odd years
9. **Renewal is a new Application** — same Board voting process
10. **No Volunteer badge** — only Colaborador/Asociado badges on dashboard

## Related Features

- [Onboarding Pipeline](16-onboarding-pipeline.md) — How tier selection fits into the signup flow
- [Tier Applications](03-asociado-applications.md) — Application entity and state machine
- [Board Voting](18-board-voting.md) — How the Board decides on tier applications
- [Coordinator Roles](17-coordinator-roles.md) — Consent check gate (Volunteer only)
- [Teams](06-teams.md) — Volunteers, Colaboradors, Asociados system teams
