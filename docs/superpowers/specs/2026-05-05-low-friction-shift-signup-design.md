# Low-Friction Shift Signup — Design

**Status:** Draft
**Date:** 2026-05-05
**Branch:** `low-friction-shift-signup`
**Supersedes (and rejects):** the gist at `https://gist.github.com/swombat/7fc3822c8aceb23fa592fb98a475f3a2` ("Shift Signup Gate – Full Document"). That spec inverted Onboarding without naming it, contradicted `docs/sections/Onboarding.md` invariants (profileless accounts redirected to Shifts), added a global URL gate that introduced per-request DB calls, used a 48-hour cancellation timer keyed on signup creation (which would silently kill real shift commitments), recorded binding signups before GDPR consent, and required a session-state bypass for empty-shift cases. This design solves the same product goal without those concessions.

---

## Goal

Get a brand-new ticket holder from "logged in for the first time" to "Pending shift signup" in three short screens, then walk them through legal consents to convert that signup to active. Every step they decline can be picked up later. The existing Onboarding flow (Profile + Consents → automatic Volunteers admission) stays the canonical activation event; this design rearranges the *order* in which the user encounters those steps so emotional commitment to a shift comes before paperwork, while keeping the data and admission rules unchanged.

The product problem this addresses: today's onboarding is several steps long and we lose volunteers who would otherwise help. The widget collapses the steps into a guided flow and lets the shift selection happen on a tight, priority-filtered list rather than the full browse experience.

---

## User flow

**Entry point — `/Welcome` (post-ticket-purchase landing):**

`/Welcome` is the public, `[AllowAnonymous]` thank-you page TicketTailor redirects buyers to (added in peterdrier/Humans #363). It explains shift participation and offers a sign-in CTA. This widget piggybacks on it as the natural entry point for new ticket holders:

- Anonymous visitor lands on `/Welcome` from TicketTailor → reads the explainer → clicks "Sign in" → goes to `Account/Login?returnUrl=/OnboardingWidget` (today: `returnUrl=/Shifts`) → completes auth → `/OnboardingWidget` dispatcher routes them to Step 1.
- Authenticated active member visiting `/Welcome` → redirected to `/Shifts` (existing behavior, unchanged).
- Authenticated non-active visitor (mid-widget, has clicked through `/Welcome` once before) → redirected to `/OnboardingWidget` (today: falls through to the explainer view; we change this so they don't see the explainer twice).

**Brand-new ticket holder, never logged in:**

1. Land on `/Welcome` from TicketTailor (or a marketing link). Click the sign-in CTA.
2. Auth (Google OAuth or magic link) → `User` created, email captured, OAuth `given_name` / `family_name` captured if available. Post-auth `returnUrl` lands them on `/OnboardingWidget`, which dispatches to Step 1.
3. **Step 1 — Names.** One screen, three fields: Legal First, Legal Last, Burner Name. Pre-filled from OAuth claims when present. Submit creates `Profile` (empty for everything else) → redirect to Step 2.
4. **Step 2 — Shifts.** Default view shows priority shifts only — `Rota.Priority` ∈ {Important, Essential} ∪ understaffed (`confirmed_count < MinVolunteers`). "Show all" link expands to the full browse partial. "Not right now" button → friendly nag ("ok but be sure to come back later — it's more fun when we build this all together") → advances to Step 3 with a session-flag set so Step 2 isn't re-prompted in the same browser session. Picking ≥1 shift creates `ShiftSignup` rows in **Pending** state regardless of `Rota.Policy` (one new branch in `ShiftSignupService.SignUp` — see Architecture). Range signups in Step 2 use the existing `SignupBlockId` mechanism unchanged.
5. **Step 3 — Consents.** Required Volunteer docs listed; user signs each. Existing `ConsentService.SubmitConsentAsync` triggers — on the last required consent, `OnboardingService.SetConsentCheckPendingIfEligibleAsync` flips `ConsentCheckStatus` to `Pending` and `SyncVolunteersMembershipForUserAsync` admits the user to the Volunteers system team per the existing predicate. **One new hook**: after admission, mid-widget Pending signups are re-evaluated against `Rota.Policy`. Public-rota signups promote to Confirmed; RequireApproval-rota signups stay Pending awaiting coordinator. Redirect to Home.

**Returning user, mid-widget:**

`Home/Index` and `Guest/Index` check `IOnboardingWidgetState.GetCurrentStepAsync(userId)`. If the result is not `Complete`, redirect to `/OnboardingWidget` (the dispatcher routes from there). Otherwise behave as today.

Evaluate top-down; first match wins:

| State | currentStep |
|-------|-------------|
| Has all required Volunteer consents | `Complete` |
| No Profile | `Names` |
| Profile, no current-event signup, no shift-skip session flag | `Shifts` |
| Profile, missing consents (has shift-skip flag OR has current-event signup) | `Consents` |

The first row short-circuits everyone past the gate — an active member with no current-event signup is `Complete`, not `Shifts`. The homepage profile-completion indicator handles the "you should sign up for a shift" nudge for that population separately.

**Profile-completion homepage indicator (replaces a 4th widget step):**

Step 4 ("rest of profile") is dropped from the widget. The Home dashboard renders a "Your profile is N% complete — finish it" indicator linking to `Profile/Edit`. Percentage is a simple count of populated optional fields (emergency contact 3 fields, birthday, languages, contact fields, photo, bio, location, contribution interests) over the total.

**User bails mid-widget and navigates to another URL by typing it:**

Existing `MembershipRequiredFilter` is **unchanged**. Pre-active users hit Home or Guest as today; both controllers redirect them back to the widget step they're on. Any URL on a controller in the existing `ExemptControllers` set works as today, with a site-wide banner ("Finish setting up your account →") rendered in `_Layout.cshtml` that links to the current widget step. Banner copy is neutral by default.

**Pending signup whose volunteer never returns:**

Signup sits Pending. A new "Incomplete onboarding" filter on the existing coordinator Pending list lets the coordinator chase, refuse, or voluntell-replace. No automated cleanup. No background job. No email reminder cascade. No timer.

---

## Architecture

### New surfaces (Web layer)

- `src/Humans.Web/Controllers/OnboardingWidgetController.cs` — `[Authorize]`, no role gate. Actions: `Index` (dispatcher — calls `IOnboardingWidgetState` and redirects to the appropriate step, or to `/Home` if `Complete`), `Names` (GET/POST), `Shifts` (GET, POST `SignUp`, POST `Skip`), `Consents` (GET, POST `Sign`), `Finish` (redirect to Home). The dispatcher is the canonical entry point — `/Welcome`, `Home/Index`'s redirect, `Guest/Index`'s redirect, and the layout banner all link here, not to a specific step.
- `src/Humans.Web/Views/OnboardingWidget/Names.cshtml`, `Shifts.cshtml`, `Consents.cshtml`. The Shifts view uses a slimmed wrapper around `_EventRotaTable` / `_BuildStrikeRotaTable`. The Consents view reuses the existing required-docs partial.
- `src/Humans.Web/Models/OnboardingWidget*ViewModel.cs` — one VM per step.
- `src/Humans.Web/Views/Shared/_OnboardingProgressBanner.cshtml` — rendered from `_Layout.cshtml` based on `IOnboardingWidgetState`. No-op when `currentStep == Complete`.

### New tiny query interface

```csharp
// src/Humans.Application/Interfaces/Onboarding/IOnboardingWidgetState.cs
public interface IOnboardingWidgetState
{
    Task<OnboardingWidgetStep> GetCurrentStepAsync(Guid userId, CancellationToken ct = default);
}

public enum OnboardingWidgetStep { Names, Shifts, Consents, Complete }
```

`OnboardingWidgetState` implementation lives in `src/Humans.Application/Services/Onboarding/`. Depends on `IProfileService`, `IShiftSignupService`, `IMembershipCalculator`, and `IHttpContextAccessor` (for the shift-skip session flag). Returns the first incomplete step. No new tables, no new columns.

### New shift-signup behavior

`IShiftSignupService.SignUp` (and `SignUpRange`) gain one branch at the head: if `IMembershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers)` returns false, force the resulting signup(s) to `Pending` regardless of `Rota.Policy`. The branch deviates from today's Public-rota auto-confirm only for this small population (mid-widget users by construction lack consents; the check is the simpler semantic version of "is this user pre-admission?").

New method on `IShiftSignupService`:

```csharp
Task PromoteWidgetPendingSignupsAfterAdmissionAsync(Guid userId, CancellationToken ct = default);
```

Walks the user's current-event Pending signups, promotes to `Confirmed` for Public rotas, leaves Pending for RequireApproval rotas (matching existing post-admission semantics for any newly-eligible volunteer). Hook is called from `ConsentService.SubmitConsentAsync` immediately after the existing `SyncVolunteersMembershipForUserAsync` call, in the same code path. Sequenced, not parallel.

### Existing surfaces touched

- `src/Humans.Web/Controllers/HomeController.cs` (`Index`) — at the top of the action, call `IOnboardingWidgetState.GetCurrentStepAsync`. If not `Complete`, redirect to `/OnboardingWidget`. Otherwise existing behavior.
- `src/Humans.Web/Controllers/GuestController.cs` (`Index`) — same redirect at the top. Otherwise existing behavior; Guest dashboard's other concerns (comms preferences, GDPR tools, ticket status) remain untouched.
- `src/Humans.Web/Controllers/WelcomeController.cs` (`Index`) — for authenticated visitors who are *not* active members, redirect to `/OnboardingWidget` instead of returning the explainer view. The active-member redirect to `/Shifts` and the anonymous-visitor explainer-view path are preserved.
- `src/Humans.Web/Views/Welcome/Index.cshtml` — change the sign-in CTA's `returnUrl` from `Url.Action("Index", "Shifts")` to `Url.Action("Index", "OnboardingWidget")`. Localized strings for the explainer copy are unchanged.
- `src/Humans.Web/Views/Shared/_Layout.cshtml` — render `_OnboardingProgressBanner` partial.
- `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` (`SignUp`, `SignUpRange`) — add the force-Pending branch and the new `PromoteWidgetPendingSignupsAfterAdmissionAsync` method.
- `src/Humans.Application/Services/Consent/ConsentService.cs` (`SubmitConsentAsync`) — call `PromoteWidgetPendingSignupsAfterAdmissionAsync` after the existing admission call.
- `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs` and `IShiftSignupRepository.cs` — add the read query for "user's current-event Pending signups" if not already present.
- `src/Humans.Application/Services/Shifts/ShiftManagementService.cs` (`GetBrowseModelAsync` or equivalent) — add a `priorityOnly` parameter that filters to Important/Essential rotas plus understaffed shifts. Existing browse logic untouched when `priorityOnly = false`.
- `src/Humans.Web/Models/HomeIndexViewModel.cs` (or whichever VM the Home dashboard uses) — add `ProfileCompletionPercent`. Computed in the controller from existing Profile fields.
- `src/Humans.Web/Views/Home/Index.cshtml` — render the completion indicator when `ProfileCompletionPercent < 100`.
- Coordinator's Pending-signup view (`_PendingSignupsPartial.cshtml` or wherever the existing list lives) — add an "Incomplete onboarding" filter chip that scopes to signups where the volunteer is missing required consents.
- `src/Humans.Web/Program.cs` — register `IOnboardingWidgetState` in DI.

### What this design does *not* touch

- `MembershipRequiredFilter`. Zero changes.
- `ShiftSignup` state machine. No new state. `Pending` is overloaded slightly (today: "needs coordinator approval on a RequireApproval rota"; new: also "needs the volunteer's consents to land before promotion"). Documented inline at the force-Pending branch.
- `User`, `Profile`, or `ShiftSignup` schema. No new columns. No migration.
- Background jobs. No reminder cascade, no kill timer.
- Claims transformation. The new query reads existing data (Profile, ShiftSignup, ConsentRecord) on demand from the controllers that need it (Home, Guest, Layout). It does not run on every request — only on the dashboard entry points and the layout banner render.

---

## Data and state

No new tables. No new columns. No migration.

State derivation:

| Question | Source |
|---|---|
| Has Profile? | `IProfileService.HasProfileAsync(userId)` |
| Has current-event signup (any state)? | `IShiftSignupRepository.HasAnyForCurrentEventAsync(userId)` (new — narrow EXISTS query) |
| Has all required Volunteer consents? | `IMembershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers)` (existing) |
| Did user click "Not right now" on Step 2? | `HttpContext.Session.GetString("OnboardingShiftSkip") == "true"` (per-browser-session, intentionally) |

Session-only shift-skip means: a user who skips on phone and reopens on laptop is re-prompted at Step 2. Acceptable for v1; if it becomes a real complaint, promote to a `Profile.OnboardingShiftSkippedAt` nullable column in a follow-up.

---

## Edge cases

**Existing users at deploy.**

- Active members (Profile + Volunteers admission): `currentStep = Complete`. Widget never appears.
- Mid-onboarding users with Profile but missing consents: widget catches them at Step 3. Step 1 is skipped (Profile exists). Step 2 may be shown if they have no current-event signup; "Not right now" gets them to Step 3.
- Profileless ticket-holder imports: `GuestController.Index` redirects to widget Step 1. They flow through normally. Their existing Guest dashboard concerns (comms, GDPR, tickets) remain accessible after the widget completes.

**Public-rota auto-confirm tension.**

`Rota.Policy = Public` normally auto-Confirms signups at creation. Mid-widget (or missing-consent) signups override this to `Pending`. Coordinators may now occasionally see Pending rows on Public rotas — the "Incomplete onboarding" filter on the Pending list explains why. Documented as a one-line invariant amendment in `docs/sections/Shifts.md`:

> Pending status on a Public rota indicates a mid-onboarding volunteer whose consents haven't landed yet. The signup auto-promotes to Confirmed when consents complete (or is auto-refused if the shift fills first).

**Range signups in Step 2.**

User picks a date range on a Build/Strike rota → `SignupBlockId` set, all shifts forced Pending. `PromoteWidgetPendingSignupsAfterAdmissionAsync` walks all Pending signups for the user, including the whole block. Block stays atomic — all shifts in the block promote together (or stay Pending if the rota is RequireApproval).

**Capacity races.**

User picks a shift in Step 2; shift fills before consents land. On promotion, capacity is re-checked. If full, the signup stays Pending and behaves like any late-arriving Pending signup — coordinator can refuse via the existing `RefuseAsync`. Matches the existing `ApproveRangeAsync` "auto-refuse if shift filled since request" pattern.

**Concurrent consent submission.**

Existing `ConsentService.SubmitConsentAsync` admits the user atomically on the last consent. The new `PromoteWidgetPendingSignupsAfterAdmissionAsync` call lives in the same code path, after admission, sequenced. No new concurrency surface.

**Account merge mid-widget.**

Account merge (rare, admin-driven) reassigns Pending signups via the existing `ReassignToUserAsync`. Widget state is derived from the post-merge user, no special handling needed.

**Empty shifts / browsing closed.**

`Step 2` renders the existing `BrowsingClosed.cshtml` / `NoActiveEvent` empty states when there's nothing to sign up for. The "Not right now" button is the unconditional escape hatch. No `ShiftGateBypass` session marker (the original spec's escape hatch is unnecessary here).

**Multi-device.**

Session-only shift-skip flag means the laptop session re-prompts Step 2 even if the phone session had skipped. Acceptable for v1.

**OAuth claim mismatch.**

Pre-fill is a hint. User edits Step 1 fields freely before submitting. Magic-link signups have no OAuth claims; Step 1 form starts empty.

---

## Test plan

**`OnboardingWidgetStateTests`:**

- No Profile → `Names`.
- Profile, no signups, no skip flag → `Shifts`.
- Profile, no signups, skip flag set → `Consents`.
- Profile, has Pending current-event signup, missing required consents → `Consents`.
- Profile, has consents, no signup → `Complete` (active member with no shift is normal).
- Profile, has consents, has signup → `Complete`.

**`ShiftSignupServiceTests` — force-Pending branch (key: missing-consents user):**

- User missing required consents signs up on a Public rota → signup is `Pending`, not `Confirmed`.
- User missing required consents signs up on a RequireApproval rota → signup is `Pending` (matches existing behavior).
- Active member signs up on a Public rota → `Confirmed` (existing behavior unchanged).
- Active member signs up on a RequireApproval rota → `Pending` (existing behavior unchanged).
- Range signup by a missing-consents user on a Build rota → all block shifts `Pending`.

**`PromoteWidgetPendingSignupsAfterAdmissionAsyncTests`:**

- Pending Public-rota signup + admission → promotes to `Confirmed`.
- Pending RequireApproval-rota signup + admission → stays `Pending`.
- Pending range block on Public rota + admission → all block shifts promote to `Confirmed`.
- Pending Public-rota signup whose shift filled since creation + admission → stays Pending; coordinator can refuse.
- No Pending signups → no-op (does not throw).

**Integration tests (`OnboardingWidgetIntegrationTests`):**

- Anonymous visitor lands on `/Welcome` → clicks sign-in CTA → `Account/Login` returnUrl is `/OnboardingWidget` → completes auth → dispatcher routes to `/OnboardingWidget/Names`.
- Authenticated active member visits `/Welcome` → redirected to `/Shifts` (existing behavior, not regressed).
- Authenticated mid-widget user visits `/Welcome` → redirected to `/OnboardingWidget` (does not see the explainer view a second time).
- Brand-new OAuth signup → magic link → lands on `/OnboardingWidget/Names` (not `Guest/Index`).
- Submit names → `/OnboardingWidget/Shifts` with priority filter applied.
- Pick a shift → Pending signup created → `/OnboardingWidget/Consents`.
- Sign all required consents → admission fires → Pending signup promotes per rota policy → redirect to Home.
- "Not right now" on Step 2 → no signup created → `/OnboardingWidget/Consents`. Sign consents → admission, no signup to promote.
- Bail mid-widget (close browser at Step 2) → re-login → land on `/OnboardingWidget/Shifts`.
- Bail at Step 3 → re-login → land on `/OnboardingWidget/Consents`. Pending signup unchanged.
- Returning active member with no current-event signup → no widget redirect, lands on Home.
- Profileless import logs in → `Guest/Index` redirects to widget Step 1.

**Coordinator dashboard:**

- Pending list with "Incomplete onboarding" filter → returns only Pending signups whose volunteers are missing required consents.

**Banner:**

- Layout renders banner when `currentStep != Complete` on any page.
- Banner not rendered when `currentStep == Complete`.

---

## What this spec deliberately does not include

- **No `User.HasCommittedToShift` column or `RegistrationSource` enum.** Widget state is derived from existing data.
- **No `PreOnboarding` flag, `HoldExpiresAt`, or `[AllowDuringOnboarding]` attribute.** The widget is a routing-and-UX change, not an authorization change. `MembershipRequiredFilter` is untouched.
- **No 48-hour kill timer or reminder email cascade.** Pending signups are a coordinator concern, surfaced via a filter on the existing list.
- **No `ShiftGateBypass` session marker.** "Not right now" is a normal POST that updates a session flag and advances the user; the empty-shift case is just the existing `BrowsingClosed` view rendered inside Step 2.
- **No new `ShiftSignup` state.** Pending is overloaded; documented inline at the force-Pending branch and in `Shifts.md`.
- **No `IOnboardingStatus` leaf service** of the kind the gist proposed. `IOnboardingWidgetState` answers a different question (which step is the user on?) and is consumed only by the dashboard entry points and the layout banner — not by the auth filter on every request.
- **No claims transformation changes.** The widget query reads from services and runs only at the few call sites that need it.

If a problem emerges in production — coordinators confused by stale mid-widget Pending signups, a real ghost-volunteer rate, multi-device skip-flag complaints — we add a scoped fix then. We don't pre-build for problems we haven't seen.

---

## Estimated scope

| Area | Effort |
|---|---|
| `IOnboardingWidgetState` interface + impl | XS |
| `OnboardingWidgetController` + 3 views + 3 VMs | S |
| Site-wide banner partial + layout integration | XS |
| `ShiftSignupService.SignUp` force-Pending branch + `PromoteWidgetPendingSignupsAfterAdmissionAsync` | S |
| `ShiftManagementService` priority-only filter | XS |
| `Home/Index` + `Guest/Index` redirect-into-widget | XS |
| Profile-completion percent on Home | XS |
| Coordinator Pending-list "Incomplete onboarding" filter | XS |
| `WelcomeController` redirect for non-active visitors + Welcome view returnUrl swap | XS |
| Tests (state, force-Pending, promotion, integration, /Welcome) | M |
| Docs: `Onboarding.md` / `Shifts.md` invariant amendments + `docs/features/24-ticket-vendor-integration.md` `/Welcome` section update | XS |

Total: **small-to-medium.** One PR.
