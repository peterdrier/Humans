# Onboarding Pipeline

## Business Context

The onboarding pipeline defines the end-to-end journey from signup to active membership. All humans follow the same initial path: sign up, complete profile, then two parallel tracks: sign legal documents and pass a Consent Coordinator profile review. Both must be complete before the human is added to the Volunteers team and gains full app access. The two tracks can happen in any order.

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
Two parallel tracks (either order):
    ├── Sign Required Consents (Volunteers team docs)
    └── Consent Coordinator profile review → Cleared or Flagged
    │
    ▼ [Both complete: Cleared + All docs signed]
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
- Sign required legal documents (can happen before or after profile review)
- Consent Coordinator clears profile review → IsApproved = true
- Volunteers team membership granted when BOTH profile review cleared AND all legal docs signed
- Dashboard shows "Active" status (no tier badge for Volunteer)

### US-16.2: New Human Onboarding (Colaborador/Asociado)

**As a** new human who wants Colaborador or Asociado membership
**I want to** apply as part of my initial signup
**So that** I don't need a separate application step

**Acceptance Criteria:**
- During profile setup, tier selector shows Colaborador/Asociado options
- Selecting a tier reveals the application form inline (motivation, etc.)
- Submitting creates both Profile and Application entities
- After both profile review cleared AND legal docs signed → Volunteer access
- Application enters Board voting queue in parallel
- **This inline experience is one-shot** — after onboarding, profile edit has no application sections

### US-16.3: Consent Check Auto-Approve

**As the** system
**I want to** automatically add humans to the Volunteers team when both profile review and legal documents are complete
**So that** there is no manual Board step for basic Volunteer access

**Acceptance Criteria:**
- Profile review clearance sets `IsApproved = true` (regardless of legal doc status)
- `SyncVolunteersMembershipForUserAsync` checks BOTH `IsApproved` AND `HasAllRequiredConsents` independently
- Volunteers team membership (and ActiveMember claim) granted only when both conditions are met
- `IsApproved = true` is intentionally set before legal docs may be complete — it marks profile review approval, not full activation. The Volunteers team membership is the true activation gate.
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

### Stage 4: Profile Review (Volunteer Gate — parallel with Stage 3)

**Trigger:** Profile exists, not yet rejected (coordinators can review at any time)
**Actor:** Consent Coordinator
**Actions:**
- Coordinator reviews profile (can proceed regardless of legal document status)
- Review queue shows legal document progress (X/Y signed) for context
- **Clear**: ConsentCheckStatus = Cleared, IsApproved = true
  - If all legal docs also signed → Volunteers team → ActiveMember
  - If legal docs still pending → admission deferred until docs are signed
- **Flag**: ConsentCheckStatus = Flagged with notes → access blocked, can be resolved later

`IsApproved = true` is set on clearance even if legal docs are incomplete — it marks profile review approval, not full activation. The Volunteers team membership is the true activation gate, and `SyncVolunteersMembershipForUserAsync` independently checks both `IsApproved` AND `HasAllRequiredConsents`.

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
7. **Immediate sync** — both profile review clearance and legal document signing trigger `SyncVolunteersMembershipForUserAsync`, which independently checks both conditions
8. **IsApproved ≠ activated** — `IsApproved = true` marks profile review approval; Volunteers team membership is the true activation gate
8. **No Volunteer badge** — only Colaborador/Asociado badges on dashboard

## Related Features

- [Membership Tiers](15-membership-tiers.md) — Tier definitions and lifecycle
- [Coordinator Roles](17-coordinator-roles.md) — Consent Coordinator role
- [Board Voting](18-board-voting.md) — Tier application voting
- [Tier Applications](03-asociado-applications.md) — Application entity and state machine
- [Volunteer Status](05-volunteer-status.md) — ActiveMember claim and gating
- [Legal Documents & Consent](04-legal-documents-consent.md) — Consent signing flow
