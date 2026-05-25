---
name: Controllers inherit shared bases for user resolution and TempData messaging
description: MVC controllers extend `HumansControllerBase`; JSON API controllers extend `ApiControllerBase`. Don't write direct `_userManager.GetUserAsync` or `TempData["..."]` calls in either.
---

Controllers that resolve the current human or set TempData messages must use the shared base classes instead of duplicating those patterns. The base is split by controller flavor so an API controller doesn't drag in view-rendering / TempData machinery it never uses.

**Rule:**
- MVC controllers (return views, use TempData, server-rendered pages) → inherit from `HumansControllerBase` (defined in `src/Humans.Web/Controllers/HumansControllerBase.cs`). Extends `Controller`.
- JSON API controllers (return `IActionResult` / `ActionResult<T>`, `[ApiController]` attribute, `/api/...` routes) → inherit from `ApiControllerBase` (defined in `src/Humans.Web/Controllers/ApiControllerBase.cs`). Extends `ControllerBase`. Intentional separation so API controllers stay slim and the LLM signal "this is an API controller" is loud.
- Use shared helpers:
  - Both bases: `GetCurrentUserAsync()`, `FindUserByIdAsync(userId)`.
  - `HumansControllerBase`: `ResolveCurrentUserOrChallengeAsync()` (challenges into the cookie-auth flow — right for MVC), `RequireCurrentUserAsync()` (NotFound), `SetSuccess`/`SetError`/`SetInfo` (TempData PRG messaging).
  - `ApiControllerBase`: `ResolveCurrentUserOrUnauthorizedAsync()` (returns 401 — right for JSON APIs).
- Do not write new direct `_userManager.GetUserAsync(User)` calls in either kind of controller when a base helper covers the case.
- Do not write direct `TempData["SuccessMessage"]`, `TempData["ErrorMessage"]`, or `TempData["InfoMessage"]` assignments in controllers.
- Do not extend bare `ControllerBase` or bare `Controller` in new controllers — pick the right shared base.

**Why:** Keeps PRG messaging, not-found handling, and user lookup behavior consistent across controllers. The MVC/API split prevents an API controller from accidentally being treated as a view-returning page (and the LLM mistake of recommending TempData helpers on an API controller).
