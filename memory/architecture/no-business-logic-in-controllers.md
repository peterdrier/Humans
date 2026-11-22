---
name: No business logic in controllers
description: Writing or reviewing a controller method: it parses, authorizes, dispatches and formats. Domain decisions go to the service. The 40-statement / complexity-15 limits are fixed.
---

A controller parses input, authorizes, calls a service and shapes the response. *A controller may change how a fact looks, never what the fact is:* sorting, filtering, formatting and time zones are presentation; tax owed, eligibility and next status are facts.

**Fine in a controller:** model binding and `ModelState` early returns, current-user lookup, view-model assembly, redirects and flash messages.

**Belongs in the service:** branching on domain state, mutating entities in a loop, computing a business value (totals, eligibility, score), multi-step orchestration.

**Enforcement.** The controller-size analyzer (HUM0031) errors on any method in a controller, actions and private helpers alike, over **40 statements or cyclomatic complexity 15**. Those numbers are fixed permanently (Peter, 2026-10-21): never propose lowering, raising or re-evaluating them. They are a backstop for the worst offenders. Logic under the thresholds is still a finding, judged by intent in section-doctor's controller-intent check. Don't dodge the limit with a private helper on the controller; move the rule to the service. A `[Grandfathered("HUM0031", …)]` method is ordinary debt.

**Why:** logic in a controller can't be tested without HTTP, can't be shared between MVC and API surfaces, and has repeatedly hidden inconsistent role checks.

**Related:** [`display-sort-in-controllers`](display-sort-in-controllers.md) · [`controller-base-conventions`](../code/controller-base-conventions.md)
