---
name: view-component-slots
description: When adding or invoking a view component across sections, or a contribution record carrying one, pass a `Type` into a host-named `[ViewComponentSlot]` seam — never a string name.
---

Invoke a view component by `Type`, never by name: `<vc:…>` (or `InvokeAsync<T>`) inside its own section; across sections, the owner contributes `typeof(XViewComponent)` to a slot the host declares with `[ViewComponentSlot(typeof(TArgs))]`, and the host calls `Component.InvokeAsync(part.Component, new TArgs(…))`.

**Why:** A string name survives a rename, a deletion or a section move and fails only at render time (or renders a blank slot); a `Type` breaks the build at the contributor (peterdrier/Humans#1815). The args record is the one place the slot's contract is written, and `ViewComponentSlotContractTests` checks it both ways: every non-optional `Invoke` parameter (and every optional one with a same-named args property) must be type-assignable, and on a single-contributor seam every args property must bind to a parameter (a dropped argument).

**How to apply:**
- Contribution records (`ChromeComponent`, `SettingsTab`, `UserPart`, …) carry `Type`, not `string`.
- The **host** names the slot, after its own page: `ChromeSlots.HeaderRight`, `UserPartSlots.BoardVoteApplicant`. The contributing section only registers into it.
- The args record must be nameable by host and contributor alike — Base or a `Contracts/` surface.
- A component used as `<vc:…>` must be `public` (HUM0034 allows view components); an internal constructor dependency (CS0051) means slot contribution or `InvokeAsync<T>` in-section, never `<vc:>` and never by name.
- HUM0036 (Error) fails the build on `InvokeAsync("Name", …)`, `Controller.ViewComponent("Name")` and `ViewComponentResult.ViewComponentName`, in C# and hand-written `.cshtml` alike. No grandfathering.

**Related:** [[view-components-vs-partials]], design-rules §8b.
