---
name: View auth — components self-resolve, callers don't pre-compute auth bools
description: Reusable views/components inject `IAuthorizationService` and resolve their own gates. Don't pass auth-derived booleans on view models from caller/component to template.
type: feedback
---

Reusable views and view components should resolve their own authorization decisions in the template via `@inject IAuthorizationService AuthService` (or in the component's view) — not via auth-derived booleans plumbed onto the view model by the caller or the component's `InvokeAsync`.

**Why:** Components that depend on the caller (or the component model) to pre-compute who-can-see-what aren't actually reusable — every new call site has to know the component's auth needs and supply the bools. A self-contained component that does its own `(await AuthService.AuthorizeAsync(User, PolicyNames.X)).Succeeded` check is a drop-in: hand it the data, it figures out what to render.

**How to apply:**
- New view components and reusable partials: prefer `@inject IAuthorizationService` + inline `AuthorizeAsync` checks in the template over adding `Can…` properties to the view model.
- Existing examples already on the target pattern: `Campaign/Detail.cshtml`, `Team/Summary.cshtml`, `Profile/Index.cshtml`, `_HumanPopover.cshtml`, `ProfileCard/Default.cshtml`.
- Legacy `Can…` booleans on view models (e.g. `ProfileCardViewModel.CanViewLegalName`, `CanSendMessage`) predate this direction. Don't backfill or refactor them just for consistency, but don't add new ones either — the direction is the other way.
- This is parallel to the service rule in [`authorization-conventions`](authorization-conventions.md): services are auth-free (auth happens before the call); views are auth-aware (auth happens in the view via `IAuthorizationService`).

**Not a hard rule** — directional. We improve over time, we haven't backfilled the existing `Can…` bools, and that's fine.
