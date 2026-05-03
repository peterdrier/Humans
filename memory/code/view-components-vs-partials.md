---
name: View Components vs Partial Views — pick by data-fetching responsibility
description: View Component when the component fetches its own data. Partial View when it's pure presentation of a model the parent already has. If the parent fetches data just to pass through, it should be a View Component.
---

ASP.NET Core offers two reusable view mechanisms. Use the right one:

**Use a View Component** (`ViewComponents/FooViewComponent.cs` + `Views/Shared/Components/Foo/Default.cshtml`) when:
- The component **fetches its own data** via injected services — the parent controller shouldn't need to know about the component's data needs
- The component has **interactive behavior** with its own JavaScript (autocomplete, search, Chart.js)
- The component is used across **multiple unrelated pages** that would each need to duplicate data-loading logic

**Use a Partial View** (`Views/Shared/_Foo.cshtml`) when:
- The component is **pure presentation** — it renders a model the parent already has in hand
- No service injection or data fetching needed
- Examples: badge rendering, status labels, simple card layouts

**The rule:** If a parent controller has to fetch data *specifically* to pass to a partial, that partial should be a View Component.

**Additional reuse rule:**
- If two or more pages share the same markup structure with only minor model/context differences, prefer a shared partial or shared page over duplicating the Razor body
- Thin wrapper views that only exist to forward to the same page shape should usually be collapsed into a shared view

**Interactive search rule:**
- If two endpoints feed the same client-side interaction pattern, prefer a shared builder/helper for the response assembly instead of duplicating result-shaping logic in each controller

**Existing View Components:** `ProfileCardViewComponent`, `NavBadgesViewComponent`, `UserAvatarViewComponent`, `TempDataAlertsViewComponent`.

**Example — wrong:**
```csharp
// Controller fetches shift data just to pass through to a partial
var shifts = await _shiftService.GetUpcomingForUser(userId);
var urgent = await _urgencyService.GetTopUrgent(5);
ViewData["ShiftCards"] = new ShiftCardsViewModel { NextShifts = shifts, UrgentShifts = urgent };
// Then in the view: @await Html.PartialAsync("_ShiftCards", ViewData["ShiftCards"])
```

**Example — right:**
```csharp
// View Component fetches its own data — controller doesn't know about shifts
// In the view: @await Component.InvokeAsync("ShiftCards")
public class ShiftCardsViewComponent : ViewComponent {
    public async Task<IViewComponentResult> InvokeAsync() {
        var userId = /* resolve from UserClaimsPrincipal */;
        var shifts = await _shiftService.GetUpcomingForUser(userId);
        return View(new ShiftCardsViewModel { ... });
    }
}
```

**Related:** [`viewcomponent-no-cache`](viewcomponent-no-cache.md) — view components must not own caching.
