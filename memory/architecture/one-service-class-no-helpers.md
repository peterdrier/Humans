---
name: one-service-class-no-helpers
description: HARD RULE. Each service is one class: `XService` implements `IXService` (and a writer interface only if another section must write). Never split it into Reader/Mutations/Notices helper classes it news up and forwards to.
---

One service per section lane, one class. `XService` implements `IXService` for reads. Only when
code outside the section genuinely has to write does the same class also implement the section's
writer interface. Nothing outside the section writes without that justification.

**Why:** Peter, 2026-11-20, after the nightly debt sweeps (from 2026-11-09) split ~40 services into
`XReader`, `XMutations`, `XNotices`, `XJournal`, … classes that the service constructs itself
(`private readonly XReader reader = new(...)`) and forwards calls to: "There should only be one
service. There should never be a pass-through." Every read and write ended up written twice, the
helpers had no interface or test seam of their own, and a reader had to open five files to follow
one method. A big service file is not debt; a forwarding layer is.

**How to apply:**
- Never answer "this service is large" by moving methods into a new class in the same section. Size
  is not a finding.
- A service never `new`s a collaborator that holds its dependencies. If it needs one, it is either
  the service itself or another section's interface injected by DI.
- Talk about interfaces first: the shape is `IXService` (+ writer interface), implemented once.
- Allowed: caching decorators over the service interface (`CachingXService`), pure static
  calculators with no injected dependencies, view-model builders in the controller layer,
  separate services that own a different lane with their own DI registration (sync jobs, webhooks).
- Found an existing helper of this kind? Fold it back into the service: move the members, delete
  the forwarding methods and the class. Don't copy it.

**Related:** [`reuse-first-change-discipline`](../process/reuse-first-change-discipline.md),
[`debt-sweep-standing-policy`](../process/debt-sweep-standing-policy.md)
