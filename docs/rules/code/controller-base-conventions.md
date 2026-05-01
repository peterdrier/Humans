---
name: Controllers inherit shared bases for user resolution and TempData messaging
description: Use `HumansControllerBase` (or specialized base) for `GetCurrentUserAsync`, `SetSuccess`/`SetError`/`SetInfo`, etc. Don't write direct `_userManager.GetUserAsync` or `TempData["..."]` calls.
---

Controllers that resolve the current human or set TempData messages must use the shared base classes instead of duplicating those patterns.

**Rule:**
- Inherit from `HumansControllerBase` or the appropriate specialized base when authenticated-user resolution or shared controller helpers are needed
- Use shared helpers such as `GetCurrentUserAsync`, `ResolveCurrentUserAsync`, `FindUserByIdAsync`, `SetSuccess`, `SetError`, `SetInfo`
- Do not write new direct `_userManager.GetUserAsync(User)` calls in controllers when a base helper already covers the case
- Do not write direct `TempData["SuccessMessage"]`, `TempData["ErrorMessage"]`, or `TempData["InfoMessage"]` assignments in controllers

**Why:** Keeps PRG messaging, not-found handling, and user lookup behavior consistent across controllers.
