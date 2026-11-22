---
name: no-leaf-to-director-callbacks
description: Reject any ctor/call edge where a leaf service (ProfileService, ConsentService) reaches up to a director (OnboardingService, etc.) — director-to-leaf is one-way only.
---

A Section service ("leaf": Users, Consent, Profile) never depends on or calls up into an Orchestrator
("director": Onboarding, lifecycle, account deletion). Orchestrator → Section is the only direction.

**Why:** directors fan out; leaves don't summon them. Extracting a "narrow query interface" so a leaf
can reach an orchestrator hides a DI cycle without fixing the inversion: it comes back with every new
method, and leaves end up double-writing state through self-referential hops. The original case hung
the first request instead of failing at startup.

**How to apply:**
- A ctor edge from `ProfileService` / `ConsentService` / `UserService` to `IOnboardingService`,
  `IHumanLifecycleService`, `IAccountDeletionService` or similar is a review reject.
- A post-write side effect on a field the leaf owns lives with the leaf; notifications go straight to
  `INotificationService`.
- Follow-up work that spans sections belongs to the orchestrator, triggered from outside the leaf:
  controllers *report* the write (`IOnboardingIntake.ProfileSavedAsync` / `ConsentSignedAsync`) and
  Onboarding owns everything that follows.
- Tells that work is in the wrong class: a "narrow query interface" created to break a cycle, or a
  `...Query` method that writes state and sends notifications.
- A cycle fails `SectionServiceResolutionTests` (`tests/Humans.Web.Tests/Infrastructure`), which
  resolves every registered service.

**Related:** [`user-profile-foundational`](user-profile-foundational.md) ·
[`orchestrator-marker`](orchestrator-marker.md)
