---
name: reuse-first-change-discipline
description: Before adding any new file, public type, service/repo method, DTO, helper, or DI registration, audit existing owners and prefer reuse; public surface needs Peter's approval.
---

New durable surface is debt until proven otherwise. Before adding a file, public type, interface
method, service or repository method, DTO or view model, helper, endpoint, dependency or DI
registration, audit the existing owner and prefer reuse or caller-side composition.

**How to apply:**
1. Identify the owning section, service or component.
2. Find the existing method, type, view or helper closest to the need.
3. Logic local to one caller is composed at that caller over existing list- or DTO-returning methods
   (`GetAllAsync()` plus a `.Where(...)`, not a new `GetSpecialCaseAsync()`).
4. Extend an existing view component, partial or page view model before creating a near-duplicate.
5. Extract shared code only with multiple live call sites, one obvious owner, and a simpler system as
   the result.
6. Still need new surface? Say which existing options you rejected and why.
7. **Stop and ask Peter** before adding public or interface surface, or a parallel service, repository
   or helper where an owner exists. Pre-approved: a contract change whose shape an existing rule
   already prescribes (listed in the PR); and `internal` types are never public surface —
   [`debt-sweep-standing-policy`](debt-sweep-standing-policy.md) D1/D2.

**Approval-required is not default-deny.** When a task needs surface that requires approval, propose
the minimal reasonable version and ask (a read-only stats view is the canonical reasonable case). Never
silently drop the feature and describe the descope afterwards as "needed approval": that turns the gate
into a veto Peter never issued.

**Why:** agent PRs relieve local friction by adding methods, helpers, DTOs and routes. Each looks
reasonable alone; together they make maintenance and review expensive.

**Related:** [`interface-method-additions-are-debt`](../architecture/interface-method-additions-are-debt.md)
