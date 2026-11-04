# Onboarding — target shape

Derived fresh each `/section-doctor` run, before any scan. This is what the section *should*
be, not a description of what it is.

## 1. What the section does

Takes a person who is new to using Humans (their account may predate them, from a ticket or a
mailing-list import) and walks them to the point where the
rest of the application opens up: give us a name, optionally pick a shift, sign the legal
documents. It decides — from data other sections own — which of those three things the person
still owes, and it puts that decision behind one URL so every other page can just say "continue
setup" without knowing where they are.

It also decides what follows a person's own profile save or signature (the coordinator's review
annotation, and withdrawing a pending deletion), and it carries out a signup reject, from
Consent's review queue or Users' admin screen: the rejection record, the account state, the team
de-provisioning and the person's notice. The coordinator's desk itself (the queue, clear, flag)
is Consent's, next to the review table it writes.

It also gives the people who never get a profile — ticket holders, imported newsletter
contacts — a page meant for them to land on.

The section stores nothing. Every fact it reads and every change it makes belongs to Users,
Consent, Teams, Shifts, Settings, Email, Notifications or AuditLog.

## 2. The shapes

Five questions. Everything in the section is one of these.

| # | Question | Answered by | Reached from |
|---|---|---|---|
| 1 | Where is this person in the funnel? | `IOnboardingWidgetState.GetCurrentStepAsync` | the widget dispatcher, the Guest dashboard, the layout banner |
| 2 | Record this step's answer and move them on | `Names` POST, `SignUp`, `SignUpRange`, `Skip`; Consent's `/Consent/Submit` for step 3 | the three step pages |
| 3 | What follows the person's own write? | `IOnboardingIntake.ProfileSavedAsync` / `ConsentSignedAsync` | reported by three controllers after a profile or consent write |
| 4 | What is left for them to sign? | `GetNextUnsignedConsentAsync` | the widget's consent step |
| 5 | This person is out | `IOnboardingIntake.RejectSignupAsync` | Consent's review detail page and Users' admin screen |

Notes the table hides:

- **Shape 1 has a second answer.** `/Welcome` routes on the `UserState` claim
  (`RoleChecks.IsActiveMember`), not on the widget step, so a person who has a name but no
  signatures is sent to `/Shifts` while every other entry point would send them back into the
  widget. That is deliberate — `UserState.Active` is the access gate and the banner picks them
  up on arrival — but it means "where is this person" has one authority for access and another
  for the funnel, and the two must not be conflated.
- **Shape 2's steps hand forward directly (Names → Shifts → Consents).** Only the consent step
  returns to the dispatcher (Consent's `Submit` lands on its return URL). The dispatcher remains
  shape 1's sole answer and the only thing that resolves a step from data.
- **Only shapes 3 and 5 are asked from outside the section.** Shape 1's answer is asked from
  inside this section and nowhere else; shape 4 has no outside caller at all. That is what
  decides which types belong on the leaf — see §3.

## 3. Structure

- **`Humans.Onboarding.Contracts` (leaf project)** — exactly the members other sections call:
  `IOnboardingIntake` (the reports Users and Consent make, and reject) and `OnboardingResult`
  (the funnel's result vocabulary, also returned by Users' own surfaces). Nothing else. The leaf
  is a project, not a folder, because Consent calls into Onboarding and Onboarding embeds
  Consent's signing step; a folder would close a cycle. It has zero project references and that
  is load-bearing.
- **`Contracts/` (folder)** — the one seam another section fills rather than calls:
  `IOnboardingShiftsStep`, which Shifts contributes. It names Shifts types, so it cannot sit on
  the leaf.
- **`Services/`** — `OnboardingService` (shapes 3–5, an orchestrator over other sections'
  service interfaces), `OnboardingWidgetState` (shape 1) behind `IOnboardingWidgetState` with
  the step enum it returns, the session seam the step resolver needs to see the "not right now"
  click, and the section's one email template with its preview. The funnel-step pair is
  `internal` and lives here rather than on the leaf: every consumer is inside this section.
- **`Controllers/`** — one per audience: the widget for the person onboarding, Guest for the
  profileless, Welcome for the anonymous visitor.
- **`Models/`** — view models and the one builder that keeps the shifts action thin.
- **`Views/` + `ViewComponents/`** — the three step pages, the guest and welcome pages, and the
  site-wide progress banner.
- **`OnboardingResource.*`** — the section's own copy, six languages.
- Root: `Section.cs` (DI, issue queue, chrome) and the two declaration lists stored rows depend
  on, `OnboardingAuditActions` and `OnboardingNotificationSources`.

Presentation the section does *not* own: the rota tables on the shifts step are Shifts'
(contributed through `IOnboardingShiftsStep`, invoked by Type), the consent step's body and form
are Consent's (`ConsentSigningStepViewComponent`), and the person and access-matrix widgets are
Base's.

## 4. Invariants

- **Admission is name + consents.** `HasRequiredNameFields && !IsSuspended && !review.IsRejected
  && HasAllRequiredConsentsForTeam(Volunteers)`, reconciled by `SystemTeamSyncJob`.
  The review's `Status` and `IsApproved` are never consulted.
  (`src/Sections/Humans.Teams/Services/SystemTeamSyncJob.cs:324`.)
- **Reject is the only coordinator action with consequences.** It records the rejection
  (Consent) and `UserState.Rejected` (Users), de-provisions the approval-gated system teams
  (Volunteers, Colaboradors, Asociados), and notifies the person.
  (`Services/OnboardingService.cs:35`; pinned on the success path by
  `RejectSignupAsync_OnSuccess_AuditsDeprovisionsAllThreeTeamsAndNotifies` in
  `tests/Humans.Onboarding.Tests/Services/OnboardingServiceTests.cs`, which fails if a team sync
  or the notification is dropped.)
- **Nothing in the funnel notifies a coordinator.** The threshold check writes the status and
  logs; the only notification the section raises is `ProfileRejected`, to the rejected person.
  `ConsentReviewNeeded` is a retired Consent source — the coordinator finds the queue through the
  `Review queue` pill in the admin sidebar (Consent's `Section.cs`), not through an inbox row.
  (`Services/OnboardingService.cs:134`;
  `src/Sections/Humans.Consent/ConsentNotificationSources.cs:14`.)
- **The name save is never gated on cross-section state.** Gating it on a step or consent
  computation loops a bare account on the Names form forever.
  (`Controllers/OnboardingWidgetController.cs:78`.)
- **The dispatcher is the canonical entry point and the only thing that resolves a step from
  data; step pages hand forward to the next page directly.** Only the consent step returns to it,
  through Consent's `Submit` return URL. (`Views/OnboardingWidget/Consents.cshtml:12`.)
- **No leaf-to-director callback.** `ProfileService` and `ConsentService` do not depend on
  Onboarding; the follow-up is a peer call from the controller after the leaf write.
  (`src/Sections/Humans.Users/Controllers/ProfileController.cs:155`;
  `src/Sections/Humans.Consent/Controllers/ConsentController.cs:102`.)
- **The section touches no `DbContext`, no repository, no cache.** (`Section.cs:24`, which
  registers services only; `Humans.Onboarding.csproj` has no data-access reference.)
- **Every rendered key resolves in the resource set the call site is bound to.** A section RCL
  binds `SharedResource` and `OnboardingResource`; a key rendered against the wrong one is
  silently its own name, in all six languages.
  (`tests/Humans.Onboarding.Tests/Architecture/OnboardingLocalizerBindingTests.cs`.)

## 5. Seams

- **Guest dashboard cards are other sections' contributions.** Comms preferences, GDPR export
  and deletion are rendered here but owned by Email and Gdpr; Tickets contributes through the
  `guest-page` chrome slot. Anything added to that page belongs to the section that owns the
  data, not here. **The page does not reach the audience it is built for.**
  `NameRequiredFilter` does not exempt `Guest`, so a profileless account asking for `/Guest` is
  sent to the name form, and `GuestController` sends anyone not yet complete back to the widget —
  which leaves the page to members who have finished setup, under a "create your profile" call
  to action. Peter ruled the page part of one deliberate onboarding redesign
  (nobodies-collective/Humans#1183), not a one-page fix.

## 6. Deliberately not done

- **No caching decorator.** The section owns no cached data; invalidation belongs to whoever
  owns the write.
- **No `IOnboardingService` on the leaf.** The next-document resolver has no consumer outside
  the section and must stay internal — the leaf is what other sections actually call, and
  nothing more.
- **No narrow `IOnboardingEligibilityQuery`-style interface for the threshold check.** That
  was a band-aid for an inverted dependency; the peer call from the controller replaced it and
  must not come back.
- **No project reference on `Humans.Onboarding.Contracts`.** Users' contracts leaf references
  this one, and Base references that; a reference added here closes a cycle three hops away.
- **No shared name-save helper between the widget and Profile edit.** `SaveProfileAsync` is a
  full-field overwrite and the widget's job is precisely to preserve the fields its form does
  not carry; folding that into a shared helper hides the reason it exists.
- **No coordinator notification on the review threshold.** The pill count is the surfacing;
  `ConsentReviewNeeded` was retired deliberately and must not be reinstated from this side.

## Load-bearing weirdness

- **`AuditEntityTypes.Profile` is a literal, not `nameof`.** The entity belongs to Users and
  the string is persisted; `nameof` across the boundary would break on a rename that nobody
  editing this section would see.
- **`OnboardingResource` is `public` while everything else in the section is `internal`.**
  The boot localization diagnostic finds section resource markers through `GetExportedTypes()`,
  and Shifts and Governance render a few of its keys.
- **Three `[Display]` label keys live in `SharedResource`, not here.** MVC's global
  `DataAnnotationLocalizerProvider` is bound to `SharedResource` and cannot see a section set.
- **`RestoreConsentSuspensionAsync` is called from a read.** `GetNextUnsignedConsentAsync`
  self-heals a consent-suspended person who has nothing left to sign; without it they loop,
  because the un-suspend otherwise only fires on a fresh signature.
- **The widget's `Names` GET prefills from the saved profile, never from OAuth claims.**
  Provider-supplied names are unverified.
- **Two retired declarations stay.** `VolunteerApproved` on both declaration lists is written by
  nothing; it is declared so stored audit rows keep their verb and stored notification rows still
  filter and purge.

---

## History

| Run | Date | PR | Headline |
|---|---|---|---|
| 1 | 2026-08-23 | #1458 | Two silently-misbound resource sets fixed; consent-step redirect loop closed; section docs re-derived from code |
| 2 | 2026-09-13 | peterdrier/Humans#1663 | `/Guest` found unreachable by the audience it exists for; false notification and Flag-is-inert claims struck from docs and comments; bulk clear stopped building the review page it never reads |
| 3 | 2026-11-04 | peterdrier/Humans#2277 | The docs catch up with the code: steps hand forward, no dashboard checklist or review gate, true csproj comments |
