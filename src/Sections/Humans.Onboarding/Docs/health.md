# Onboarding — target shape

Derived fresh each `/section-doctor` run, before any scan. This is what the section *should*
be, not a description of what it is.

## 1. What the section does

Takes a person who has just signed in for the first time and walks them to the point where the
rest of the application opens up: give us a name, optionally pick a shift, sign the legal
documents. It decides — from data other sections own — which of those three things the person
still owes, and it puts that decision behind one URL so every other page can just say "continue
setup" without knowing where they are.

Alongside that it runs the Consent Coordinator's desk: the list of people who have finished
signing and are waiting to be looked at, and the four things a coordinator can do about one of
them (clear, clear in bulk, flag, reject). Only reject changes what the person can reach; the
other three are notes for the record.

It also gives the people who never get a profile — ticket holders, imported newsletter
contacts — somewhere to land.

The section stores nothing. Every fact it reads and every change it makes belongs to Users,
Consent, Teams, Governance, Shifts, Email, Notifications or AuditLog.

## 2. The shapes

Six questions. Everything in the section is one of these.

| # | Question | Answered by | Reached from |
|---|---|---|---|
| 1 | Where is this person in the funnel? | `IOnboardingWidgetState.GetCurrentStepAsync` | the widget dispatcher, the Guest dashboard, the layout banner |
| 2 | Record this step's answer and move them on | `Names` POST, `SignUp`, `SignUpRange`, `Skip`, `SignConsent` | the three step pages |
| 3 | Did that write push them over the review threshold? | `IOnboardingIntake.SetConsentCheckPendingIfEligibleAsync` | peer-called by three controllers after a profile or consent write |
| 4 | What is left for them to sign? | `GetNextUnsignedConsentAsync` | the widget's consent step |
| 5 | Who is waiting for a coordinator, and what do I do about them? | review queue, review detail, clear / bulk clear / flag | `/OnboardingReview` |
| 6 | This person is out | `IOnboardingIntake.RejectSignupAsync` | the review detail page and Users' admin screen |

Two notes the table hides:

- **Shape 1 has a second answer.** `/Welcome` routes on the `UserState` claim
  (`RoleChecks.IsActiveMember`), not on the widget step, so a person who has a name but no
  signatures is sent to `/Shifts` while every other entry point would send them back into the
  widget. That is deliberate — `UserState.Active` is the access gate and the banner picks them
  up on arrival — but it means "where is this person" has one authority for access and another
  for the funnel, and the two must not be conflated.
- **Shape 2 always ends at the dispatcher.** No step page knows what follows it. That is the
  property that lets steps be reordered, and it is worth protecting.

## 3. Structure

- **`Contracts/` (leaf project)** — the two writes other sections make (`IOnboardingIntake`),
  the step resolver other sections read (`IOnboardingWidgetState` + `OnboardingWidgetStep`),
  and `OnboardingResult`. Nothing else. The leaf is a project, not a folder, because Consent
  calls into Onboarding and Onboarding renders Consent's copy; a folder would close a cycle.
  It has zero project references and that is load-bearing.
- **`Services/`** — `OnboardingService` (shapes 3–6, an orchestrator over other sections'
  service interfaces), `OnboardingWidgetState` (shape 1), and the session seam the step
  resolver needs to see the "not right now" click.
- **`Controllers/`** — one per audience: the widget for the person onboarding, the review
  queue for the coordinator, Guest for the profileless, Welcome for the anonymous visitor.
- **`Models/`** — view models and the one builder that keeps the shifts action thin.
- **`Views/` + `ViewComponents/`** — the three step pages, the two review pages, the guest and
  welcome pages, and the site-wide progress banner.
- **`OnboardingResource.*`** — the section's own copy, six languages.
- Root `Section*.cs` files are the composition entry points (DI, admin nav, chrome).

Presentation the section does *not* own: the rota tables on the shifts step are Shifts'
(`OnboardingShiftsListViewComponent`, invoked by name), the consent body is Consent's
(`_ConsentReviewBody`), and the person/profile/access-matrix widgets are Base's and Users'.

## 4. Invariants

- **Admission is name + consents.** `HasRequiredNameFields && !IsSuspended && RejectedAt is
  null && HasAllRequiredConsentsForTeam(Volunteers)`, reconciled by `SystemTeamSyncJob`.
  `ConsentCheckStatus` and `IsApproved` are never consulted.
- **Clear and Flag change nothing but the record *for a Volunteer*.** No team sync, no email,
  no access change — admission ignores `IsApproved` (above). They are *not* inert for a
  Colaborador/Asociado: `RecordConsentCheck` sets `Profile.IsApproved = (status == Cleared)`
  (`Humans.Users` `UserService`) and `SystemTeamSyncJob` gates the two tier teams on that flag,
  so `Cleared → Flagged` drops a tier member from their tier team on the next hourly sync.
  That is why the detail view still withholds Flag from a cleared human — see §5.
- **Reject is the only coordinator action with consequences.** It sets `RejectedAt`,
  de-provisions the three approval-gated system teams, and notifies the person.
- **A flagged, unresolved person stays in the queue** even if an override set `IsApproved`.
  A rejected person leaves it — Clear is refused on them, so the row would be unresolvable.
- **A merged tombstone never appears in the queue.**
- **The name save is never gated on cross-section state.** Gating it on a step or consent
  computation loops a bare account on the Names form forever.
- **Every step page redirects to the dispatcher, never to a named next step.**
- **No leaf-to-director callback.** `ProfileService` and `ConsentService` do not depend on
  Onboarding; the threshold check is a peer call from the controller after the leaf write.
- **The section touches no `DbContext`, no repository, no cache.**
- **Every rendered key resolves in the resource set the call site is bound to.** A section RCL
  binds `SharedResource`, `OnboardingResource` and `ConsentResource`; a key rendered against
  the wrong one is silently its own name, in all six languages.

## 5. Seams

- **Guest dashboard cards are other sections' contributions.** Comms preferences, GDPR export
  and deletion are rendered here but owned by Users; Tickets contributes through the
  `guest-page` chrome slot. Anything added to that page belongs to the section that owns the
  data, not here.
- **A cleared human can still be rejected; Flag stays withheld until a cross-section fix.**
  Settled by Peter on 2026-08-23: cause can surface after the fact, so the coordinator needs
  somewhere to act on it, and Reject is the verb he named. The service was always permissive —
  the restriction lived only in the detail view, which means every other caller already had
  what the page withheld. Reject is now exposed for a cleared human and the gate is the
  `ConsentCoordinatorBoardOrAdmin` policy on the action, not the markup. Do not add a
  service-side refusal for it.

  Flag is the exception, and not because the view is the rule: `Cleared → Flagged` clears
  `Profile.IsApproved`, which `SystemTeamSyncJob` reads as tier-team eligibility (§4), so
  exposing it would make an audit annotation silently kick a Colaborador/Asociado out of their
  tier team. Volunteers admission was already carved out of `IsApproved` for exactly this
  reason; the tier path never was. Expose Flag once that is settled — either `Flagged` stops
  writing `IsApproved`, or tier eligibility stops reading it. Both changes live in
  `Humans.Users`/`Humans.Teams`, not here.

## 6. Deliberately not done

- **No caching decorator.** The section owns no cached data; invalidation belongs to whoever
  owns the write.
- **No `IOnboardingService` on the leaf.** The review queue, its DTOs, the clear/flag pair and
  the next-document resolver have no consumer outside the section and must stay internal —
  the leaf is two methods and two types wide on purpose.
- **No narrow `IOnboardingEligibilityQuery`-style interface for the threshold check.** That
  was a band-aid for an inverted dependency; the peer call from the controller replaced it and
  must not come back.
- **No project reference on `Humans.Onboarding.Contracts`.** Users' contracts leaf references
  this one, and Base references that; a reference added here closes a cycle three hops away.
- **No shared name-save helper between the widget and Profile edit.** `SaveProfileAsync` is a
  full-field overwrite and the widget's job is precisely to preserve the fields its form does
  not carry; folding that into a shared helper hides the reason it exists.

## Load-bearing weirdness

- **`AuditEntityTypes.Profile` is a literal, not `nameof`.** The entity belongs to Users and
  the string is persisted; `nameof` across the boundary would break on a rename that nobody
  editing this section would see.
- **`OnboardingResource` is `public` while everything else in the section is `internal`.**
  The boot localization diagnostic finds section resource markers through `GetExportedTypes()`.
- **Three `[Display]` label keys live in `SharedResource`, not here.** MVC's global
  `DataAnnotationLocalizerProvider` is bound to `SharedResource` and cannot see a section set.
- **`RestoreConsentSuspensionAsync` is called from a read.** `GetNextUnsignedConsentAsync`
  self-heals a consent-suspended person who has nothing left to sign; without it they loop,
  because the un-suspend otherwise only fires on a fresh signature.
- **The widget's `Names` GET prefills from the saved profile, never from OAuth claims.**
  Provider-supplied names are unverified.

---

## History

| Run | Date | PR | Headline |
|---|---|---|---|
| 1 | 2026-08-23 | #1458 | Two silently-misbound resource sets fixed; consent-step redirect loop closed; section docs re-derived from code |
