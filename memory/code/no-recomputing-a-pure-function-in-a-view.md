---
name: Never recompute in a view what a pure function already computes
description: A `.cshtml` that re-derives a value a service/domain function already computed is a latent bug — carry the computed value through the DTO instead.
---

If a pure function already computes a value, the view renders that value — it never computes it a second time.

**Why:** Both copies compile, so nothing in the build, the tests or any scan can see them disagree. They agree on the day they are written and drift the first time the C# side gains a case, and the symptom is a page showing the wrong answer while every test stays green. Duplicated logic in two C# files at least shows up as duplication; duplicated logic split across C# and Razor does not.

**How to apply:**

- When a `.cshtml` is about to branch on or calculate something the domain or service layer already decides, add the decided value to the view model and render it.
- The same holds for a partial or view component that re-derives what its caller already knows: pass it in.
- This is not an argument for logic-free views as an aesthetic — formatting, ordering and presentation choices belong in the view. It is about *decisions*: eligibility, totals, visibility, status.
- **Authorization is the one exception, and it runs the other way.** Visibility that follows from an authorization policy is resolved *in* the view through `@inject IAuthorizationService`, never handed down as a `Can…` boolean on the view model — see [`auth-in-views-self-resolving`](auth-in-views-self-resolving.md). This rule is about the domain decisions a pure function has already made; that one is about who may see them.

**Related:** [`auth-in-views-self-resolving`](auth-in-views-self-resolving.md) for the authorization carve-out, and `docs/architecture/design-rules.md` on controllers translating rather than deciding — the same reason, one layer up.
