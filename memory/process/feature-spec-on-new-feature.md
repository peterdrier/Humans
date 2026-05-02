---
name: Create a feature spec when implementing a new feature
description: When implementing a non-trivial new feature, create a `docs/features/<feature>.md` spec in the same PR as the implementation. Covers create-new; `post-fix-doc-check.md` covers update-existing.
---

When implementing a non-trivial new feature, **create** a corresponding spec file at `docs/features/<feature-name>.md` in the same PR as the implementation.

**Why:** Feature specs are the durable record of *intent* — the business context, user stories, and acceptance criteria the code implements. Without one, the next person to touch the feature (human or agent) only sees the *what* (code) and not the *why* (intent), which leads to plausible-but-wrong refactors that satisfy the code surface but break the original product reasoning. The spec is also the diffable target for `/spec-review` and `/spec-check` skills.

**How to apply:**

- Trigger: a new controller action, new entity, new workflow / state machine, or new significant cross-section integration. Bug fixes and small refinements don't need a spec.
- Create the file under `docs/features/` using the same general shape as existing specs in that directory:
  - **Business context** — why this exists; which stakeholder asked; what problem it solves.
  - **User stories** with acceptance criteria.
  - **Data model** — entities, fields, relationships introduced or modified.
  - **Workflows / state machines** if applicable.
  - **Related features** — cross-links to other spec docs and to the relevant `docs/sections/<section>.md` invariant doc.
- Commit the spec in the same PR as the implementation, not as a follow-up. Splitting them defeats the point — a feature without its spec immediately drifts.
- If the change updates an *existing* feature, see [`post-fix-doc-check.md`](post-fix-doc-check.md) for the update-existing flow.

**Related:** [`post-fix-doc-check.md`](post-fix-doc-check.md), [`docs/sections/SECTION-TEMPLATE.md`](../../docs/sections/SECTION-TEMPLATE.md).
