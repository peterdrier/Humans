# Onboarding Pipeline

## Business Context

The onboarding pipeline defines the end-to-end journey from signup to active membership. All humans follow the same initial path: sign up, complete profile, sign consents. After that, a Consent Coordinator performs a safety check. Once cleared, the human is **automatically approved as a Volunteer** and gains full app access.

During initial signup only, humans who want Colaborador or Asociado membership see the application form inline alongside their profile setup. This is a streamlined one-shot experience — the system creates both a Profile and an Application behind the scenes, but the user fills one unified form. The tier application proceeds through Board voting in parallel — it never blocks Volunteer access.

The consent check is purely a **Volunteer-level gate**. It has nothing to do with tier applications.

### Key Change from Previous Model

Previously, Board members manually set `IsApproved = true` on each profile. The new model introduces a Consent Coordinator safety gate that automatically approves Volunteers after clearance. Board members now only vote on Colaborador/Asociado tier applications.

## Pipeline Overview

```
Sign Up (Google OAuth)
    │
    ▼
Profile Setup (one-shot onboarding experience)
    ├── Basic profile info (name, location, etc.)
    ├── [Optional] Tier selection → Colaborador/Asociado application form inline
    │
    ▼
Sign Required Consents (Volunteers team docs)
    │
    ▼
[Auto] ConsentCheckStatus → Pending
    │
    ▼
Consent Coordinator reviews → Cleared or Flagged
    │
    ▼ [Cleared]
    │
    ├── ALL humans → IsApproved = true → Volunteers team → ActiveMember ✓
    │
    └── If Colaborador/Asociado Application exists → Board Voting queue (parallel)
            │
            ├── Board approves → Colaboradors/Asociados team (2-year term)
            └── Board rejects → notification, stays as Volunteer
```

## User Stories

### US-16.1: New Human Onboarding (Volunteer)

**As a** new human choosing Volunteer tier (or no tier)
**I want to** complete onboarding with minimal steps
**So that** I can start participating quickly

**Acceptance Criteria:**
- Sign up via Google OAuth
- Fill basic profile
- Sign required legal documents
- System sets ConsentCheckStatus to Pending
- Consent Coordinator clears → IsApproved = true → Volunteers team
- Dashboard shows "Active" status (no tier badge for Volunteer)

### US-16.2: New Human Onboarding (Colaborador/Asociado)

**As a** new human who wants Colaborador or Asociado membership
**I want to** apply as part of my initial signup
**So that** I don't need a separate application step

**Acceptance Criteria:**
- During profile setup, tier selector shows Colaborador/Asociado options
- Selecting a tier reveals the application form inline (motivation, etc.)
- Submitting creates both Profile and Application entities
- After consent check clearance → Volunteer access immediately
- Application enters Board voting queue in parallel
- **This inline experience is one-shot** — after onboarding, profile edit has no application sections

### US-16.3: Consent Check Auto-Approve

**As the** system
**I want to** automatically approve humans as Volunteers after consent check clearance
**So that** there is no manual Board step for basic Volunteer access

**Acceptance Criteria:**
- ConsentCheckStatus = Cleared triggers:
  - `IsApproved = true`
  - `SyncVolunteersMembershipForUserAsync` → Volunteers team
  - ActiveMember claim on next request
- Consent check is purely a Volunteer gate — independent of any tier application
- Board approval is only for Colaborador/Asociado (via Board voting)

### US-16.4: View Onboarding Progress

**As a** new human
**I want to** see a checklist of what I need to do
**So that** I know my progress

**Acceptance Criteria:**
- Dashboard shows "Getting Started" checklist:
  1. ✓/○ Complete your profile
  2. ✓/○ Sign required documents
  3. ✓/○ Safety check (Pending / Cleared)
- For Colaborador/Asociado applicants, additional indicator:
  - Tier application status (Submitted / Under Review / Approved / Rejected)
- Quick links to relevant pages

## Pipeline Stages

### Stage 1: Sign Up

**Trigger:** First Google OAuth login
**Actions:**
- User record created
- Profile record created (empty, MembershipTier = Volunteer)
- Redirected to Profile Setup (onboarding mode)

### Stage 2: Profile Setup (One-Shot Onboarding)

**Trigger:** User visits profile for the first time (onboarding mode)
**Actions:**
- General Info: name, pronouns, location, bio, contact fields
- Tier selection: Volunteer (default), Colaborador, Asociado
- If Colaborador/Asociado → application form appears inline (motivation, etc.)
- Emergency contact, Board notes (optional)
- On submit:
  - Profile saved (MembershipTier set)
  - If Colaborador/Asociado → Application entity created (Status = Submitted)
- **After initial onboarding, profile edit shows profile fields only — no tier selector or application form**

### Stage 3: Legal Consents

**Trigger:** User visits Consent page
**Actions:**
- Shows Volunteers team required documents
- User reviews and signs each
- When ALL required consents signed:
  - ConsentCheckStatus auto-set to Pending (if currently null)
  - Consent Coordinator notified

### Stage 4: Consent Check (Volunteer Gate)

**Trigger:** ConsentCheckStatus = Pending
**Actor:** Consent Coordinator
**Actions:**
- Coordinator reviews profile and consent status
- **Clear**: ConsentCheckStatus = Cleared → IsApproved = true → Volunteers team → ActiveMember
- **Flag**: ConsentCheckStatus = Flagged with notes → access blocked, can be resolved later

This gate is about Volunteer access only. It does not evaluate tier applications.

### Stage 5: Board Voting (Colaborador/Asociado Only)

**Trigger:** Application exists AND consent check Cleared
**Actor:** Board members
**Actions:**
- Application appears on Board Voting dashboard
- Board members vote, then finalize decision
- Approve → Colaboradors/Asociados team with term
- Reject → notification, human remains Volunteer

## Migration: Existing Users

### Grandfather Clause

Existing approved users are **not** retroactively required to go through consent check. The new consent check gate only applies to humans who sign up **after** this functionality is deployed.

**Migration logic:**
- Existing `Profile.IsApproved = true` → set `ConsentCheckStatus = Cleared`, `MembershipTier = Volunteer`
- Existing approved Asociado Applications → set `Application.MembershipTier = Asociado`, compute `TermExpiresAt`
- No disruption to existing active volunteers — they keep their access

### New Signups Only

After deployment, new humans go through the consent check pipeline. Existing humans who were already approved bypass it entirely.

## Dashboard Views

### Pre-Approval (New Human)
```
Getting Started
├── [✓] Complete your profile          → /Profile/Edit
├── [○] Sign required documents         → /Consent
├── [○] Safety check (pending)
│
│   If Colaborador/Asociado selected:
└── [○] Tier application: Submitted
```

### Post-Approval (Active)
```
Welcome back, [Name]!
Status: Active

[Colaborador badge]  ← only if Colaborador/Asociado
Term expires: Dec 31, 2027

[Teams]  [Governance]  [Profile]
```

## Business Rules

1. **Consent check = Volunteer gate** — independent of tier applications
2. **Inline application is one-shot** — only during initial signup
3. **After onboarding, profile edit is just profile** — no tier/application sections
4. **Volunteer access is never blocked by tier applications** — parallel tracks
5. **Grandfather existing users** — consent check only for new signups after deployment
6. **Auto-Pending** — system sets ConsentCheckStatus to Pending when all consents are signed
7. **Immediate sync** — consent check clearance triggers immediate Volunteers team sync
8. **No Volunteer badge** — only Colaborador/Asociado badges on dashboard

## Related Features

- [Membership Tiers](15-membership-tiers.md) — Tier definitions and lifecycle
- [Coordinator Roles](17-coordinator-roles.md) — Consent Coordinator role
- [Board Voting](18-board-voting.md) — Tier application voting
- [Tier Applications](03-asociado-applications.md) — Application entity and state machine
- [Volunteer Status](05-volunteer-status.md) — ActiveMember claim and gating
- [Legal Documents & Consent](04-legal-documents-consent.md) — Consent signing flow
