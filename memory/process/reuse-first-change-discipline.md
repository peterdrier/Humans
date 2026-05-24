---
name: Reuse existing surface before adding durable surface
description: Before adding new files, public types, service/repository methods, DTOs/view models, helpers, endpoints, dependencies, or DI registrations, audit the existing owner/surface and prefer reuse or local composition; public/interface surface requires Peter approval.
---

New durable surface is technical debt until proven otherwise. Before adding any new file, public type, interface method, service/repository method, DTO/view model, helper, endpoint, dependency, or DI registration, audit the existing owner and prefer reuse, caller-side composition, or a small local LINQ/mapping chain.

**Why:** Agent-generated PRs tend to solve local friction by adding methods, helpers, DTOs, services, and routes. Each addition looks reasonable in isolation, but the accumulated surface makes later maintenance and review expensive. The project already enforces interface method budgets; this rule applies the same reuse-first discipline to every other durable surface.

**How to apply:**

1. Identify the owning section/service/component before adding anything.
2. List the existing method/type/view/component/helper that is closest to the need.
3. Prefer caller-side composition on existing list-returning or DTO-returning methods when the extra logic is local to one caller.
4. Extract shared code only when there are multiple live call sites, one obvious owner, and the extraction makes the net system simpler.
5. If new surface is still necessary, state which existing options were rejected and why.
6. Stop and ask Peter before adding public/interface surface, or before adding a parallel service/repository/helper where an owner already exists.

**Examples:**

- Prefer `GetAllAsync()` + `.Where(...).FirstOrDefault()` at one call site over `GetSpecialCaseAsync()` on an interface.
- Prefer extending an existing view component or partial over creating a near-duplicate component.
- Prefer adding a field to an existing view model only when that view model already owns the page; do not create a parallel model just to avoid touching call-site mapping.

**Related:**
- [`../architecture/interface-method-additions-are-debt.md`](../architecture/interface-method-additions-are-debt.md)
