---
name: No business logic in controllers — controllers are HTTP adapters
description: Controllers parse input, authorize, dispatch to services, return responses. Branching on domain state, computing derived values, or coordinating multi-step domain operations belongs in a service. Enforced by analyzer HUM0031 — any controller method (action or private helper) with > 34 statements or cyclomatic complexity > 15 is a build error.
---

Controllers parse input, authorize, dispatch to a service, and return a response. They do **not** contain business logic — branching on domain state, computing derived values, or coordinating multi-step domain operations.

**Why:** Controllers are HTTP adapters. Mixing domain concerns in makes them un-testable (require HTTP context to exercise), un-reusable across surfaces (the API and MVC controllers should be able to share the same domain calls), and impossible to authorize uniformly (the same domain rule has to be re-checked at every adapter). The thicker the controller, the harder it is to keep authorization rules consistent across the codebase. Past instance: every refactor that pulled logic *out* of a controller and into a service revealed already-inconsistent role checks across surfaces.

**Enforcement** — analyzer `HUM0031` (`ControllerBusinessLogicAnalyzer`, in-editor + build): every method in a `ControllerBase`-derived type — actions **and** private helpers — is measured semantically. Statements > 34 **or** cyclomatic complexity > 15 → Error; offenders may carry method-level `[Grandfathered("HUM0031", …)]` to ride as Warning. **There are currently no grandfathers** — the ~15 the analyzer was calibrated against were all refactored (nobodies-collective/Humans#1156), and the statement dial was then ratcheted 40 → 34, the current high-water mark (nobodies-collective/Humans#857). If the action does view-model assembly + dispatch only, statements + complexity stay tiny. If it's running a state machine, it's the wrong file.

The complexity dial is at its floor at 15 and should not be lowered. The metric counts switch-expression arms, ternaries and `??`, which is what pure display mapping is made of — `GuideController.DisplayName`, a one-expression stem→label lookup, scores 14. Every remaining top scorer is formatting, which the hard rules assign to the controller. Lowering it would flag formatting as business logic and push it into services. Ratchet the statement dial instead.

**What does NOT count as business logic:**

- Model binding & argument validation (`!ModelState.IsValid` early return).
- Claims extraction, current-user lookup, current-tenant resolution.
- View-model assembly (mapping a service result to a view-shaped DTO).
- Auth shortcuts (`User.IsInRole(...)` early returns; full auth still goes through `[Authorize]` + handlers).
- Redirects and `TempData` flash messages.

**What does:**

- "If state == X and role == Y and date < Z, do A; else do B." — that's a rule, push it down.
- Looping over a collection to mutate domain entities. — service.
- Computing a derived business value (totals, eligibility, score). — service.
- Multi-step orchestration (load A, mutate B, schedule C). — service.

**How to apply:**

- New action method drifting past 34 statements? Look for the rule; extract a service method.
- New action method getting `if`/`switch`/loop heavy? Same — extract.
- Don't try to game the threshold by extracting a private helper *on the controller*; the rule is about layer ownership, not file size. Helpers belong in the service or its dispatcher.

**Related:** [`no-linq-at-db-layer`](no-linq-at-db-layer.md), [`controller-base-conventions`](../code/controller-base-conventions.md), [`authorization-conventions`](../code/authorization-conventions.md).
