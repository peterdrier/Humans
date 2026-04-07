# RIP: Profileless Account Access (#377)
## Generated: 2026-04-07

## 1. Vision & Purpose

### Elevator Pitch

**For** the Nobodies community beyond current volunteers (ticket holders, newsletter subscribers, curious visitors)
**Who** interact with the org but aren't in Humans
**Profileless Accounts** is a lightweight account tier
**That** brings them into the system for smart communication targeting and gives them a natural path to full membership
**Unlike** today, where the only way into Humans is full volunteer onboarding
**Our approach** creates a minimal account layer that enables audience segmentation (has ticket? has profile? both? neither?) without forcing everyone through the same funnel

### Success Headlines (6-month horizon)
1. **Community:** "Every ticket holder and newsletter subscriber can log into Humans and manage their preferences"
2. **Organization:** "Board can see which ticket holders aren't volunteers yet and target recruitment; ticket holders aren't spammed with ticket promos they don't need"
3. **Technical:** "Single system of record for all community contacts — one User account accumulates layers over time (mailing list, tickets, profile)"

### Anti-Goals
- NOT building ticket transfer/management (requires profiles on both sides — separate feature)
- NOT replacing MailerLite as email sending platform — this is about audience data, not email infrastructure
- NOT gating public content behind auth — camps, public team pages stay public
- NOT forcing profileless accounts through any onboarding steps beyond initial auth
- NOT building the actual import jobs here — this feature provides the infrastructure they'll use

### Success Criteria

| Criterion | Measure | Priority |
|-----------|---------|----------|
| Profileless users have a functional dashboard | GuestController serves comms prefs, GDPR tools, ticket status, create-profile CTA | Must-have |
| Audience segmentation is possible | Can query: has-ticket, has-profile, both, neither, by event year | Must-have |
| Profileless → volunteer conversion path | "Create profile" CTA → existing onboarding flow | Must-have |
| Communication preferences self-service | Profileless users can manage their own prefs | Must-have |
| GDPR self-service with event hold | Data export works; deletion request acknowledged but held pre-event, cancelled on profile creation | Must-have |
| Account pre-creation infrastructure | "Find or create User by email" logic ready for import jobs | Must-have |
| Feedback submission | Profileless users can submit feedback | Should-have |
| Unsubscribe-as-auth | Email unsubscribe links land in authenticated experience | Should-have |

## 2. Stakeholders & Context

### Stakeholder Map

| Stakeholder | Interest | Influence | Key Needs |
|-------------|----------|-----------|-----------|
| Peter | Sole developer, architect | High | Buildable fast, clean architecture |
| Board | Community visibility, recruitment targeting | High | "Show me who has tickets but isn't a volunteer" |
| Comms team | Audience targeting | Medium | Don't spam ticket holders with ticket promos |
| Ticketing coordinator | Ticket holder visibility | Medium | See who has tickets, who doesn't |
| Volunteer coordinators | Recruitment funnel | Medium | Convert ticket holders to volunteers |
| Ticket holders (non-members) | Minimal friction | Low influence, high interest | Easy login, manage preferences, see tickets |
| Newsletter subscribers | Even less engaged | Low | Unsubscribe works, GDPR compliance |

### Constraints

**Hard:**
- Single developer — phased delivery essential
- Existing clean architecture must be respected (Domain/Application/Infrastructure/Web layers)
- Must not break existing volunteer onboarding or auth flows
- GDPR compliance (consent tracking, data export, deletion — with event hold for ticket holders)
- Consent records are immutable (append-only, DB triggers prevent UPDATE/DELETE)

**Soft:**
- No new external dependencies preferred
- Keep EF migrations minimal and safe

### Reusable Assets
- `User.Profile` already nullable — no schema change for core relationship
- `MagicLinkService` exists and handles signup for new accounts
- `CommunicationPreference` is per-User (account), not per-Profile
- `User.PreferredLanguage` already exists (defaults to "en"), language switcher persists it
- GDPR export (`ProfileService.ExportDataAsync`) already handles null Profile
- Ticket matching (`BuildEmailLookupAsync`) resolves against ALL UserEmails
- Authorization policies (`IsActiveMember`, role checks) already Profile/role-aware
- `MembershipRequiredFilter` already has exempt controller list
- `authorize-policy` tag helper already gates nav items
- `ProcessAccountDeletionsJob` already exists
- Feedback submission already works without a profile (Feedback is exempt from membership filter)

### Key Dependency
This feature must ship before ticket import and MailerLite import jobs, as it provides the account infrastructure those imports will use.

## 3. Scope & Requirements

### Capability Map

| # | Capability | Complexity | Priority |
|---|-----------|------------|----------|
| 1 | **GuestController + routing** — new controller for profileless dashboard, redirect profileless users here | M | Must |
| 2 | **Nav filtering** — profileless users see: Home, Camps, Teams (public), Legal, Guest Dashboard. Hide member-only items | S | Must |
| 3 | **Guest dashboard** — landing page with comms prefs, GDPR tools, ticket status, create-profile CTA | M | Must |
| 4 | **Comms preference self-service** — manage communication preferences without a profile | S | Must |
| 5 | **GDPR data export** — download your data without a profile (already works, needs UI access) | S | Must |
| 6 | **GDPR deletion request with event hold** — request deletion, held pre-event, cancelled on profile creation | M | Must |
| 7 | **Ticket status display** — show matched ticket orders on dashboard (order details if available, else "you have a ticket") | M | Must |
| 8 | **Account pre-creation infrastructure** — idempotent "find or create User by email" service for import jobs | M | Must |
| 9 | **Create-profile transition** — CTA → ProfileController.Edit (existing initial setup flow) | S | Must |
| 10 | **Routing guard update** — profileless users can't access profile-gated features, get redirected to Guest dashboard | S | Must |
| 11 | **Profileless feedback submission** — already works, may need minor UI adjustments | S | Should |
| 12 | **Unsubscribe-as-auth** — email unsubscribe links land in authenticated profileless experience | M | Should |
| 13 | **Audience segmentation admin view** — gauges/filters for has-ticket/has-profile/both/neither by event year | M | Should |

### Scope Boundary

**In Scope:**
- GuestController + profileless dashboard + routing
- Nav filtering for profileless users
- Comms preferences, GDPR tools, ticket status on dashboard
- Account pre-creation service (find-or-create by email)
- GDPR deletion with event hold + profile-creation cancellation
- Create-profile CTA → existing onboarding

**Out of Scope:**
- Actual ticket import job (separate issue, uses infrastructure from here)
- Actual MailerLite import job (separate issue, uses infrastructure from here)
- Ticket transfer/management (needs profiles on both sides)
- Bulk invite/magic link send mechanism
- MailerLite integration changes
- Email sending infrastructure

**Boundary Decisions:**
- **Account pre-creation service is in scope** even though import jobs aren't — it's the shared infrastructure that must exist first
- **Feedback submission: in scope as should-have** — already mostly works, just needs UI accessibility for profileless users
- **Audience segmentation admin view: should-have** — the data model enables it, but the admin UI can follow
- **GDPR deletion: must-have with event hold** — can't delete ticket holder data pre-event; creating a profile cancels pending deletion request

### Key User Stories

**1. Ticket holder first login**
> When I receive a magic link email, I click it, authenticate, and land on a dashboard showing my ticket status, communication preferences, and a "Want to get involved? Create a profile" button.
> AC: Login works, dashboard loads, ticket info displays, no profile creation required.

**2. Newsletter subscriber manages preferences**
> When I log in via magic link, I can see and change which types of communications I receive.
> AC: Comms preferences load for my account, changes persist, timestamp updated.

**3. Profileless user requests GDPR deletion**
> When I request account deletion from the dashboard, I see confirmation that my request is recorded. If I have tickets for an upcoming event, I'm told the deletion will be processed after the event.
> AC: Request stored, eligibleAfter date set if pre-event ticket exists, job processes after date.

**4. Profileless user decides to become a volunteer**
> When I click "Create Profile" on the dashboard, I enter the existing profile creation flow and then the normal volunteer onboarding pipeline.
> AC: ProfileController.Edit opens in initial-setup mode, existing onboarding continues from there. Pending deletion request (if any) is cancelled.

**5. Import job creates account**
> When ticket data is imported and the email has no existing account, a User + UserEmail is created with DisplayName from ticket data and appropriate ContactSource. If the email already has an account, ticket data is layered onto the existing account.
> AC: Idempotent find-or-create, no duplicate accounts, ContactSource tracks data origin.

## 4. Architecture & Technical Options

### Key Architectural Decisions

**ADR-1: GuestController for profileless experience**
- **Context:** Profileless users need a landing page. HomeController already branches on profile status but mixing two dashboard concepts adds complexity.
- **Decision:** New `GuestController` with own view models. Added to MembershipRequiredFilter exempt list.
- **Rationale:** Clean separation. GuestController owns the profileless UX entirely. Easy to reason about, test, and evolve independently.
- **Consequences:** One more controller, but a clear boundary. MembershipRequiredFilter redirects profileless users to Guest/Index instead of Home/Index.

**ADR-2: Nav filtering for profileless users**
- **Context:** Nav uses `authorize-policy` tag helpers and `IsAuthenticated` checks. Profileless users are authenticated but not active members.
- **Decision:** Profileless users see: Guest Dashboard, Camps, Teams (public version), Legal. Everything else hidden. City Planning and Budget (currently guarded by `IsAuthenticated`) get tighter guards.
- **Rationale:** Most items already correctly hidden via policy. Only City Planning and Budget need fixing — they use `IsAuthenticated` which would incorrectly show them to profileless users.
- **Consequences:** Teams routing must ensure profileless users see the public view, not the authenticated member view. Add a `HasProfile` check or equivalent to City Planning and Budget nav items.

**ADR-3: Pre-create accounts at import time**
- **Context:** Accounts could be created on-demand at first login, or pre-created when data enters the system.
- **Decision:** Pre-create User + UserEmail during import (ticket import, MailerLite import). Idempotent "find or create by email" logic. Data layers accumulate: a single account can have MailerLite subscription + ticket records + eventually a Profile.
- **Rationale:** Simpler overall. The universe of people maps to the account concept. Enables gauges (how many accounts have tickets, profiles, both, neither). Magic link login for these users just finds the existing account — no signup form needed.
- **Consequences:** Import jobs become the primary account creation point. Need robust dedup by email. ContactSource tracking must handle multiple sources per account. Multi-year ticket records enable "had 2026 ticket, no 2027 yet" targeting.

**ADR-4: GDPR deletion with event hold and profile abort**
- **Context:** GDPR requires deletion on request, but can't delete ticket holder data pre-event (legitimate interest basis). Users who create a profile are re-engaging.
- **Decision:** Deletion request stored as a record (timestamp, eligibleAfter date). Pre-event: acknowledged, nothing happens. Post-event: `ProcessAccountDeletionsJob` processes eligible requests. Creating a profile cancels the pending deletion request.
- **Rationale:** Transparent to the user (they see status and reasoning). Profile creation is an affirmative act that supersedes deletion intent.
- **Consequences:** Need a `DeletionRequest` entity or fields on User. ProcessAccountDeletionsJob needs to check eligibility dates. Profile creation flow needs to check for and cancel pending deletion requests.

**ADR-5: "Create Profile" transition via existing flow**
- **Context:** Need a profileless → volunteer conversion path.
- **Decision:** CTA on Guest dashboard links to `ProfileController.Edit`, which already handles `profile is null` as initial-setup mode (line 190: `isInitialSetup = profile is null || !profile.IsApproved || preview`).
- **Rationale:** Zero new flow. The existing onboarding pipeline (profile → consent → approval) handles the rest.
- **Consequences:** After profile creation, `MembershipRequiredFilter` takes over and guides through remaining onboarding.

### Data Flow

```
MailerLite Import ──→ FindOrCreateByEmail ──→ User + UserEmail
                                              ↑ sets comms opt-in timestamp
                                              ↑ ContactSource: "MailerLite"

Ticket Import ──────→ FindOrCreateByEmail ──→ User + UserEmail
                                              ↑ links TicketOrder/Attendee
                                              ↑ ContactSource: "Ticket" (or adds to existing)

Magic Link Login ───→ Finds existing User ──→ Signs in ──→ GuestController
                  └─→ (cold signup: creates User via existing flow)

Guest Dashboard ────→ Shows: comms prefs, GDPR, ticket status, create-profile CTA

Create Profile ─────→ ProfileController.Edit ──→ Normal onboarding pipeline
                  └─→ Cancels pending deletion request
```

### Technical Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Views/partials assume profile exists → NPE | Medium | Medium | Audit views reachable by authenticated users (Home, Feedback, shared partials) |
| Teams nav routes to member view instead of public | Low | Medium | Explicit routing check: if no profile, use public team routes |
| Duplicate account creation during import | Medium | Medium | FindOrCreateByEmail uses UserEmail lookup, not just User.Email |
| GDPR deletion hold confuses users | Low | Low | Clear messaging: why, when, and that profile creation cancels it |

## 5. Prototyping & Risk Validation

Given the scale (small nonprofit, single dev, well-understood codebase), formal prototyping is unnecessary. The riskiest assumptions are:

| Assumption | Confidence | Validation |
|-----------|-----------|------------|
| Existing views don't NPE for profileless users | Likely | Audit during implementation — test each reachable page as profileless user |
| MembershipRequiredFilter redirect change is safe | Likely | Existing test coverage + manual verification |
| FindOrCreateByEmail dedup works across UserEmails | Likely | Unit tests with multiple-email scenarios |
| Teams public vs. member routing is clean | Uncertain | Verify during nav filtering implementation — may need a condition in TeamsController |

**Go/No-Go:** If any view assumes Profile exists in a way that can't be guarded with a simple null check, that view needs refactoring first. Not a blocker — just sequencing.

## 6. Implementation Plan

### Delivery Phases

**Phase A: GuestController Shell + Routing (1 issue)**

Build the empty shell so profileless users have somewhere to land.

- New `GuestController` with `Index` action returning a basic dashboard view
- Add to `MembershipRequiredFilter` exempt list
- Update `MembershipRequiredFilter`: if authenticated + no profile → redirect to `Guest/Index` (not `Home/Index`)
- Basic Guest dashboard view: welcome message, placeholder sections
- Guest layout/nav: show Guest Dashboard, Camps, Teams (public), Legal; hide member-only items
- Fix City Planning and Budget nav items: change from `IsAuthenticated` to require profile/active membership
- Ensure Teams nav links route profileless users to public team pages

Exit criteria: A user with no profile can log in via magic link, lands on Guest dashboard, sees correct nav, can browse public pages. Profile-gated features are inaccessible.

**Phase B: Dashboard Content (1 issue)**

Fill in the Guest dashboard with real functionality.

- Communication preferences panel (reuse `CommunicationPreferenceService`, new view on GuestController)
- GDPR data export link/action (reuse existing `ProfileService.ExportDataAsync`)
- Ticket status panel (query TicketOrders/Attendees for user's emails, display order info or "you have a ticket")
- "Create Profile" CTA → link to `ProfileController.Edit`
- Language switcher (already works, just ensure visible in Guest layout)

Exit criteria: Profileless user can view/edit comms preferences, export their data, see ticket status, and click through to profile creation.

**Phase C: GDPR Deletion with Event Hold (1 issue)**

- Deletion request UI on Guest dashboard
- Store deletion request: `RequestedAt`, `EligibleAfter` (set to post-event date if ticket exists for upcoming event), `CancelledAt`
- Display status: pending, held until [date], or cancelled
- `ProfileController.Edit` (POST): on profile creation, cancel any pending deletion request
- Update `ProcessAccountDeletionsJob` to check `EligibleAfter` date and skip cancelled requests
- EF migration for deletion request fields/entity

Exit criteria: Profileless user can request deletion. Ticket holders see hold message. Profile creation cancels request. Job processes eligible requests post-event.

**Phase D: Account Pre-Creation Infrastructure (1 issue)**

- `FindOrCreateUserByEmailAsync` service method in Application/Infrastructure layer
- Lookup against all UserEmails (not just User.Email) for dedup
- Creates User + UserEmail with: DisplayName (from caller — attendee name, contact name, or email prefix), ContactSource
- Handles layering: if account exists, doesn't re-create, returns existing User
- ContactSource tracking: supports multiple sources per account (MailerLite + Ticket is valid)
- Unit tests: create new, find existing by primary email, find existing by secondary email, multiple sources

Exit criteria: Import jobs can call `FindOrCreateUserByEmailAsync` and get correct behavior. No duplicate accounts created.

**Phase E: Polish & Should-Haves (1-2 issues)**

- Feedback submission: verify profileless users can submit, fix any UI gaps (display name attribution uses `User.DisplayName`)
- Unsubscribe-as-auth: email unsubscribe links authenticate the user and land on Guest dashboard comms preferences
- Audience segmentation: admin view with gauges — total accounts, with ticket, with profile, both, neither, by event year
- Edge case audit: test all reachable views as profileless user, fix any NPEs

Exit criteria: All should-have features work. No NPEs for profileless users on any reachable page.

### Phase Dependencies

```
Phase A (shell + routing)
  └──→ Phase B (dashboard content)
  └──→ Phase C (GDPR deletion) — can parallel with B
  └──→ Phase D (pre-creation infra) — can parallel with B and C

Phase E (polish) — after A, B, C, D are done
```

Phases B, C, and D are independent of each other and can be parallelized or done in any order after A.

### Milestones

| Milestone | Verification | Unlocks |
|-----------|-------------|---------|
| Phase A complete | Profileless user logs in, sees Guest dashboard, correct nav | All other phases |
| Phase D complete | FindOrCreateUserByEmailAsync unit tests pass | Ticket import and MailerLite import jobs (separate issues) |
| Phases A-D complete | Full profileless experience works end-to-end | Phase E polish + import job development |
| Phase E complete | All should-haves work, edge case audit clean | Feature ready for production |

### EF Migration Notes

- Phase C likely needs a migration: deletion request fields (or new entity) on User
- Phase D may need a migration: ContactSource tracking if not already on User
- Follow the EF migration reviewer gate before committing any migration

## Appendix: Key Decisions Log

| # | Decision | Rationale |
|---|----------|-----------|
| 1 | GuestController (not HomeController branch) | Clean separation, own view models, easier to reason about |
| 2 | Pre-create accounts at import time (not on-demand at login) | Simpler overall, enables gauges, accounts are the unifying concept |
| 3 | Data layers accumulate on one account | MailerLite + ticket + profile are additive, not exclusive. Same person can have all three |
| 4 | GDPR deletion held pre-event for ticket holders | Legitimate interest basis — can't delete ticket data before the event |
| 5 | Profile creation cancels pending deletion | Affirmative act of engagement supersedes deletion intent |
| 6 | Nav shows public Teams view for profileless users | Teams has distinct public/member views; profileless users see public |
| 7 | DisplayName for auto-created accounts from ticket/contact data | Email prefix as fallback for cold signups |
| 8 | Language preference already on User entity | `PreferredLanguage` persists via existing language switcher — no new work needed |
| 9 | Feedback submission already works without profile | FeedbackController is exempt from MembershipRequiredFilter — just needs UI polish |
| 10 | Driver is community management, not GDPR compliance | Optimized for audience segmentation and recruitment funnel, not just data rights |
