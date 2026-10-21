---
name: No business logic in controllers — controllers are HTTP adapters
description: Controllers parse input, authorize, dispatch, and return — no domain branching or orchestration. HUM0031 errors on any controller method over 40 statements or complexity 15.
---

Controllers parse input, authorize, dispatch to a service, and return a response. They do **not** contain business logic — branching on domain state, computing derived values, or coordinating multi-step domain operations.

**Why:** Controllers are HTTP adapters. Mixing domain concerns in makes them un-testable (require HTTP context to exercise), un-reusable across surfaces (the API and MVC controllers should be able to share the same domain calls), and impossible to authorize uniformly (the same domain rule has to be re-checked at every adapter). The thicker the controller, the harder it is to keep authorization rules consistent across the codebase. Past instance: every refactor that pulled logic *out* of a controller and into a service revealed already-inconsistent role checks across surfaces.

**Enforcement** — analyzer `HUM0031` (`ControllerBusinessLogicAnalyzer`, in-editor + build): every method in a `ControllerBase`-derived type — actions **and** private helpers — is measured semantically. Statements > 40 **or** cyclomatic complexity > 15 → Error; pre-existing offenders carry method-level `[Grandfathered("HUM0031", …)]`. The thresholds are deliberately generous (calibrated to the ~15 worst offenders at introduction). If the action does view-model assembly + dispatch only, statements + complexity stay tiny. If it's running a state machine, it's the wrong file.

**Fixed at 40/15, judged by intent (Peter, 2026-10-21).** The thresholds are a backstop and never ratchet — see [[hum0031-fixed-at-40-15]]. A method under them can still carry business logic; that is judged by intent, in section-doctor's controller-intent check on every run, using the litmus *would a Hangfire job doing the same domain operation need this line?* (the June audit, `docs/debt/controller-intent-audit-2026-06-12.md`, is the worked example). peterdrier/Humans#1156 cleared all 15 grandfathers; one that reappears is ordinary debt.

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

- New action method drifting past 40 statements? Look for the rule; extract a service method.
- New action method getting `if`/`switch`/loop heavy? Same — extract.
- Don't try to game the threshold by extracting a private helper *on the controller*; the rule is about layer ownership, not file size. Helpers belong in the service or its dispatcher.

**Related:** [`no-linq-at-db-layer`](no-linq-at-db-layer.md), [`controller-base-conventions`](../code/controller-base-conventions.md), [`authorization-conventions`](../code/authorization-conventions.md).
