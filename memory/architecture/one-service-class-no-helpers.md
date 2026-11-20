---
name: one-service-class-no-helpers
description: HARD RULE. Each service is one class implementing its interface(s); never split it into Reader/Mutations/Notices helper classes it news up and forwards to. A big section splits into separate services along natural lines, each its own DI registration, with one writer per cached graph.
---

A service is one class. `XService` implements `IXService` for reads; only when code outside the
section genuinely has to write does the same class also implement the section's writer interface.

A section may have more than one service when its work falls into natural, separate jobs (Finance:
category accounts, creditors, SEPA payouts). Each such service:
- owns a job you can name without code nouns, and the tables or workflow that go with it;
- is registered in DI on its own and reached through an interface (internal is fine), never `new`ed;
- calls a sibling service through that sibling's interface, the same way another section would;
- never forwards: a method whose body only calls another service's method is deleted, and the
  caller uses that service directly.

**Caches pin the line.** When a caching decorator holds a graph (`CachingTeamService` holds teams and
members), every write to that graph goes through the one service behind that decorator. A split may
move out only work whose writes the cache does not hold.

**Size.** Aim for a service an agent can read in one go (about 25k tokens). Reforge's `classSize`
charge says a class is big; it does not say where to cut. If no natural line exists, a big service
stays big and the reason goes in the section's `health.md` load-bearing list.

**Why:** Peter, 2026-11-20. The nightly debt sweeps (from 2026-11-09) split ~40 services into
`XReader`, `XMutations`, `XNotices`, … classes the service constructed itself and forwarded to:
"There should only be one service. There should never be a pass-through." Then, on what replaces
them: "some sections have a lot going on … we don't want to split that code up in ways that's
unnatural … for the ones that are big, is there a logical place to separate them? Let's do that."

**How to apply:**
- Never answer "this class is large" by moving methods into a helper the service constructs.
- Allowed beside services: caching decorators over the service interface, pure static calculators
  with no injected dependencies, view-model builders in the controller layer.
- Found a constructed helper? Move its members into the service that owns the job and delete the
  forwarders.

**Related:** [`reuse-first-change-discipline`](../process/reuse-first-change-discipline.md),
[`debt-sweep-standing-policy`](../process/debt-sweep-standing-policy.md)
