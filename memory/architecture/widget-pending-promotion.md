---
name: Widget Pending → Confirmed promotion
description: Mid-onboarding shift signups created inside `/OnboardingWidget` are forced to Pending status until the user's required Volunteer consents land, then auto-promoted to Confirmed.
---

Shift signups created from inside the `/OnboardingWidget` Step 2 priority-shift list are forced to `Pending` regardless of the rota's `Policy` (Public or RequireApproval). When the user later completes consents, `ConsentService.SubmitConsentAsync` calls `IShiftSignupService.PromoteWidgetPendingSignupsAfterAdmissionAsync`, which promotes those mid-widget Pending rows on Public rotas to `Confirmed`. Pending signups on `RequireApproval` rotas are left alone — coordinator review still owns those.

**Why:** The widget lets a user grab shifts before their consents are signed (the whole point of the low-friction flow), but admission to Volunteers — and therefore the right to a Confirmed shift slot — still requires consents. Forcing Pending and promoting on consent-complete keeps the admission invariant intact (`Confirmed` ⇒ admitted Volunteer) without forcing the user back through a re-confirmation step. It also overloads the existing `Pending` status rather than inventing a new state, so coordinator review queues, capacity counts, and the state machine all keep working unchanged.

**How to apply:**

- The force-Pending decision is on the **signup creation path** inside the widget controller / service, not on the rota. Public rotas elsewhere in the app continue to create `Confirmed` signups directly for active members.
- Promotion is **only for the mid-widget cohort**: identify by signups whose owning user is not yet an active Volunteer at signup time AND whose rota policy is `Public`. Coordinator-approval Pending rows on `RequireApproval` rotas are out of scope.
- Promotion fires from `ConsentService.SubmitConsentAsync` on the same call path that admits the user to Volunteers (after `SyncVolunteersMembershipForUserAsync`). Don't add a separate background sweep — the consent-submit moment is the trigger.
- Capacity is re-checked at promotion time. If the shift filled in the meantime, leave the signup `Pending` and let coordinator review / range-approve logic handle it the same way it handles any other over-capacity Pending row.
- See `docs/sections/Shifts.md` (invariants) and `docs/sections/Onboarding.md` (invariants) for the user-facing description of these two Pending sources.

**Related:** [`docs/sections/Shifts.md`](../../docs/sections/Shifts.md), [`docs/sections/Onboarding.md`](../../docs/sections/Onboarding.md).
