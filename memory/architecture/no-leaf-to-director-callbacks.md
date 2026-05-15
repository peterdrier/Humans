---
name: No leaf-to-director callbacks
description: Reject any ctor or call edge where a leaf service (ProfileService, ConsentService, etc.) reaches up to a director (OnboardingService, AdminDashboardService, etc.). Director-to-leaf is one-way.
---

Leaf services never depend on or call back into directors. If you find yourself extracting a "narrow query interface" so a leaf can summon an orchestrator — stop. The predicate or side-effect is housed in the wrong class. Move it to the leaf that owns the field, or to the call site that already drives the leaf.

**Why:** Directors fan out; leaves don't summon them. The narrow-interface band-aid pattern (`IXxxEligibilityQuery` etc.) papers over a DI cycle without fixing the inversion. The cycle keeps coming back as new methods get added, and the resulting runtime call graph has leaves double-writing state through synchronous self-referential service hops. Two real incidents:

- **`IOnboardingEligibilityQuery` (removed 2026-05-15).** `ProfileService.SaveProfileAsync` and `ConsentService.SubmitConsentAsync` both reached back into `OnboardingService` to run a consent-check threshold (predicate + state write on `Profile.ConsentCheckStatus` + Consent Coordinator notification). The DI cycle (`ProfileService ↔ OnboardingService` via `IProfileService`) crashed startup every time a new method was added on either side. The fix was relocating the threshold check to `IProfileService.TrySetConsentCheckPendingIfEligibleAsync` — Profile owns the `ConsentCheckStatus` field, so the predicate + write belong there. Both leaves now self-trigger; no Onboarding reference.

**How to apply:**

- Treat any ctor edge from `ProfileService` / `ConsentService` / `UserService` to `IOnboardingService`, `IHumanLifecycleService`, `IAccountDeletionService`, etc. as a code-review reject.
- If a leaf wants to fire a post-write side-effect, the predicate + write live with the leaf (it owns the field). Notifications go through `INotificationService` directly. Director services observe state on their own surfaces (review queues, jobs, UI entry points) — they don't get poked by leaves.
- Symptom check: extracting a "narrow query interface" to break a cycle, or naming a method something-Query that actually writes state and dispatches notifications. Both are tells that the work is in the wrong class.
- Director-to-leaf calls remain normal: `OnboardingService` writes through `IProfileService` during clear/flag/reject/approve. That direction is correct.

Related: [[user-profile-foundational]] (Profile is foundational — nothing reaches up out of it).
